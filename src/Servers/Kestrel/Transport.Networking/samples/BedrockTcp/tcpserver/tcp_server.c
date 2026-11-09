// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A plain C TCP server with no TLS and no HTTP. It reads --request-bytes from a connection, then writes
// --response-bytes back, and repeats. Equal sizes make it an echo server, a small request with a large response
// makes it a download server and a large request with a small response makes it an upload sink.
//
// Linux:   one blocking accept thread hands connections to --threads epoll workers (level triggered).
// Windows: one blocking accept thread hands connections to --threads WSAPoll workers.
//
// Build (Linux):   gcc -O2 -pthread -o tcp_server tcp_server.c
// Build (Windows): gcc -O2 -pthread -o tcp_server.exe tcp_server.c -lws2_32
// Run:             tcp_server --port 5601 --threads 8 --request-bytes 64 --response-bytes 1024 --cpus 16-31
// Windows only:    --poll-ms <n> sets the WSAPoll timeout (default 1, 0 spins).

#ifndef _WIN32
#define _GNU_SOURCE
#endif

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <pthread.h>

#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
typedef SOCKET sock_t;
#define SOCK_INVALID INVALID_SOCKET
#define sock_close closesocket
#define SEND_FLAGS 0
static int would_block(void) { return WSAGetLastError() == WSAEWOULDBLOCK; }
#else
#include <sched.h>
#include <errno.h>
#include <unistd.h>
#include <fcntl.h>
#include <arpa/inet.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/epoll.h>
#include <sys/socket.h>
typedef int sock_t;
#define SOCK_INVALID (-1)
#define sock_close close
#define SEND_FLAGS MSG_NOSIGNAL
static int would_block(void) { return errno == EAGAIN || errno == EWOULDBLOCK; }
#endif

#define RECV_BUFFER_BYTES (256 * 1024)
#define MAX_CONNECTIONS_PER_WORKER 4096

typedef struct conn
{
    sock_t fd;
    size_t request_received;
    size_t response_sent;
    int sending;
    int registered_out;
    struct conn* next;
} conn;

typedef struct worker
{
    int index;
    char* recv_buffer;
#ifdef _WIN32
    CRITICAL_SECTION lock;
    conn* pending;
    conn* active[MAX_CONNECTIONS_PER_WORKER];
    int active_count;
#else
    int epfd;
#endif
} worker;

static size_t request_bytes = 64;
static size_t response_bytes = 1024;
static char* response_buffer;
static int thread_count = 8;
#ifndef DEFAULT_POLL_MS
#define DEFAULT_POLL_MS 1
#endif
static int poll_ms = DEFAULT_POLL_MS;
static worker* workers;

static void set_nonblocking(sock_t fd)
{
#ifdef _WIN32
    u_long on = 1;
    ioctlsocket(fd, FIONBIO, &on);
#else
    fcntl(fd, F_SETFL, fcntl(fd, F_GETFL, 0) | O_NONBLOCK);
#endif
}

static void set_nodelay(sock_t fd)
{
    int one = 1;
    setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, (const char*)&one, sizeof(one));
}

static void apply_affinity(const char* cpus)
{
    if (cpus == NULL || *cpus == '\0')
    {
        return;
    }
#ifdef _WIN32
    unsigned long long mask = 0;
#else
    cpu_set_t set;
    CPU_ZERO(&set);
#endif
    char* copy = strdup(cpus);
    for (char* part = strtok(copy, ","); part != NULL; part = strtok(NULL, ","))
    {
        int from = atoi(part);
        char* dash = strchr(part, '-');
        int to = dash != NULL ? atoi(dash + 1) : from;
        for (int cpu = from; cpu <= to; cpu++)
        {
#ifdef _WIN32
            mask |= 1ULL << cpu;
#else
            CPU_SET(cpu, &set);
#endif
        }
    }
    free(copy);
#ifdef _WIN32
    SetProcessAffinityMask(GetCurrentProcess(), (DWORD_PTR)mask);
#else
    sched_setaffinity(0, sizeof(set), &set);
#endif
}

// Returns 0 when the connection stays open and -1 when it must be closed.
// On a partial send the connection switches to write interest until the response is flushed.
static int try_send(conn* c)
{
    while (c->response_sent < response_bytes)
    {
        int n = (int)send(c->fd, response_buffer + c->response_sent, (int)(response_bytes - c->response_sent), SEND_FLAGS);
        if (n < 0)
        {
            if (would_block())
            {
                c->sending = 1;
                return 0;
            }
            return -1;
        }
        c->response_sent += (size_t)n;
    }
    c->sending = 0;
    c->response_sent = 0;
    return 0;
}

static int on_readable(worker* w, conn* c)
{
    for (;;)
    {
        size_t want = request_bytes - c->request_received;
        if (want > RECV_BUFFER_BYTES)
        {
            want = RECV_BUFFER_BYTES;
        }
        int n = (int)recv(c->fd, w->recv_buffer, (int)want, 0);
        if (n == 0)
        {
            return -1;
        }
        if (n < 0)
        {
            return would_block() ? 0 : -1;
        }
        c->request_received += (size_t)n;
        if (c->request_received >= request_bytes)
        {
            c->request_received = 0;
            if (try_send(c) < 0)
            {
                return -1;
            }
            if (c->sending)
            {
                return 0;
            }
        }
        if ((size_t)n < want)
        {
            return 0;
        }
    }
}

static int on_writable(conn* c)
{
    return try_send(c);
}

#ifdef _WIN32

static void* worker_main(void* arg)
{
    worker* w = (worker*)arg;
    WSAPOLLFD fds[MAX_CONNECTIONS_PER_WORKER];
    for (;;)
    {
        EnterCriticalSection(&w->lock);
        while (w->pending != NULL && w->active_count < MAX_CONNECTIONS_PER_WORKER)
        {
            conn* c = w->pending;
            w->pending = c->next;
            w->active[w->active_count++] = c;
        }
        LeaveCriticalSection(&w->lock);

        if (w->active_count == 0)
        {
            Sleep(1);
            continue;
        }

        for (int i = 0; i < w->active_count; i++)
        {
            fds[i].fd = w->active[i]->fd;
            fds[i].events = w->active[i]->sending ? POLLWRNORM : POLLRDNORM;
            fds[i].revents = 0;
        }
        int ready = WSAPoll(fds, (ULONG)w->active_count, poll_ms);
        if (ready <= 0)
        {
            continue;
        }
        for (int i = 0; i < w->active_count; i++)
        {
            conn* c = w->active[i];
            int close_it = 0;
            if (fds[i].revents & (POLLERR | POLLHUP | POLLNVAL))
            {
                close_it = 1;
            }
            else if (fds[i].revents & POLLWRNORM)
            {
                close_it = on_writable(c) < 0;
            }
            else if (fds[i].revents & (POLLRDNORM | POLLRDBAND))
            {
                close_it = on_readable(w, c) < 0;
            }
            if (close_it)
            {
                sock_close(c->fd);
                free(c);
                w->active[i] = w->active[--w->active_count];
                fds[i] = fds[w->active_count];
                i--;
            }
        }
    }
    return NULL;
}

static void hand_off(worker* w, conn* c)
{
    EnterCriticalSection(&w->lock);
    c->next = w->pending;
    w->pending = c;
    LeaveCriticalSection(&w->lock);
}

#else

static void update_interest(worker* w, conn* c)
{
    int want_out = c->sending;
    if (want_out == c->registered_out)
    {
        return;
    }
    struct epoll_event ev;
    ev.events = want_out ? EPOLLOUT : EPOLLIN;
    ev.data.ptr = c;
    epoll_ctl(w->epfd, EPOLL_CTL_MOD, c->fd, &ev);
    c->registered_out = want_out;
}

static void* worker_main(void* arg)
{
    worker* w = (worker*)arg;
    struct epoll_event events[256];
    for (;;)
    {
        int ready = epoll_wait(w->epfd, events, 256, -1);
        if (ready < 0)
        {
            if (errno == EINTR)
            {
                continue;
            }
            return NULL;
        }
        for (int i = 0; i < ready; i++)
        {
            conn* c = (conn*)events[i].data.ptr;
            int close_it = 0;
            if (events[i].events & (EPOLLERR | EPOLLHUP))
            {
                close_it = 1;
            }
            else if (events[i].events & EPOLLOUT)
            {
                close_it = on_writable(c) < 0;
            }
            else if (events[i].events & EPOLLIN)
            {
                close_it = on_readable(w, c) < 0;
            }
            if (close_it)
            {
                epoll_ctl(w->epfd, EPOLL_CTL_DEL, c->fd, NULL);
                sock_close(c->fd);
                free(c);
            }
            else
            {
                update_interest(w, c);
            }
        }
    }
    return NULL;
}

static void hand_off(worker* w, conn* c)
{
    struct epoll_event ev;
    ev.events = EPOLLIN;
    ev.data.ptr = c;
    epoll_ctl(w->epfd, EPOLL_CTL_ADD, c->fd, &ev);
}

#endif

int main(int argc, char** argv)
{
    int port = 5601;
    const char* cpus = NULL;
    for (int i = 1; i + 1 < argc; i += 2)
    {
        if (strcmp(argv[i], "--port") == 0)
        {
            port = atoi(argv[i + 1]);
        }
        else if (strcmp(argv[i], "--threads") == 0)
        {
            thread_count = atoi(argv[i + 1]);
        }
        else if (strcmp(argv[i], "--request-bytes") == 0)
        {
            request_bytes = (size_t)atoll(argv[i + 1]);
        }
        else if (strcmp(argv[i], "--response-bytes") == 0)
        {
            response_bytes = (size_t)atoll(argv[i + 1]);
        }
        else if (strcmp(argv[i], "--cpus") == 0)
        {
            cpus = argv[i + 1];
        }
        else if (strcmp(argv[i], "--poll-ms") == 0)
        {
            poll_ms = atoi(argv[i + 1]);
        }
        else
        {
            fprintf(stderr, "unknown argument %s\n", argv[i]);
            return 2;
        }
    }
    if (request_bytes == 0 || response_bytes == 0 || thread_count <= 0)
    {
        fprintf(stderr, "request-bytes, response-bytes and threads must be positive\n");
        return 2;
    }

#ifdef _WIN32
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
#endif
    apply_affinity(cpus);

    response_buffer = (char*)malloc(response_bytes);
    memset(response_buffer, 'x', response_bytes);
    workers = (worker*)calloc((size_t)thread_count, sizeof(worker));
    for (int i = 0; i < thread_count; i++)
    {
        workers[i].index = i;
        workers[i].recv_buffer = (char*)malloc(RECV_BUFFER_BYTES);
#ifdef _WIN32
        InitializeCriticalSection(&workers[i].lock);
#else
        workers[i].epfd = epoll_create1(0);
#endif
        pthread_t thread;
        pthread_create(&thread, NULL, worker_main, &workers[i]);
        pthread_detach(thread);
    }

    sock_t listener = socket(AF_INET, SOCK_STREAM, 0);
#ifndef _WIN32
    int one = 1;
    setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));
#endif
    struct sockaddr_in address;
    memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons((unsigned short)port);
    if (bind(listener, (struct sockaddr*)&address, sizeof(address)) != 0 || listen(listener, 1024) != 0)
    {
        fprintf(stderr, "bind/listen failed on port %d\n", port);
        return 1;
    }
    printf("READY port=%d threads=%d request=%zu response=%zu\n", port, thread_count, request_bytes, response_bytes);
    fflush(stdout);

    int next = 0;
    for (;;)
    {
        sock_t fd = accept(listener, NULL, NULL);
        if (fd == SOCK_INVALID)
        {
            continue;
        }
        set_nodelay(fd);
        set_nonblocking(fd);
        conn* c = (conn*)calloc(1, sizeof(conn));
        c->fd = fd;
        hand_off(&workers[next], c);
        next = (next + 1) % thread_count;
    }
}
