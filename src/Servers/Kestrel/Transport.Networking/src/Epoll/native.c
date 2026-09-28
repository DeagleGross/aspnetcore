// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <netinet/tcp.h>
#include <openssl/err.h>
#include <openssl/ssl.h>
#include <sched.h>
#include <stdint.h>
#include <sys/epoll.h>
#include <sys/eventfd.h>
#include <unistd.h>

// Normalize Linux's packed epoll_event and OpenSSL's thread-local error result at the ABI.
struct ready_event { uint64_t id; uint32_t events; uint32_t reserved; };
struct tls_result { int status, length, pending, error; uint64_t reason; };
enum { COMPLETE, WANT_READ, WANT_WRITE, CLOSED, PEER_ABORT, FAILED };

int ep_set_cpu(int cpu)
{
    if (cpu < 0) return 0;
    if (cpu >= CPU_SETSIZE) return -EINVAL;
    cpu_set_t set; CPU_ZERO(&set); CPU_SET(cpu, &set);
    return sched_setaffinity(0, sizeof(set), &set) ? -errno : 0;
}

int ep_create(void)
{
    int fd = epoll_create1(EPOLL_CLOEXEC);
    return fd < 0 ? -errno : fd;
}

int ep_listener(int port)
{
    int fd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
    if (fd < 0) return -errno;
    int one = 1;
    struct sockaddr_in address = { .sin_family = AF_INET, .sin_port = htons(port), .sin_addr.s_addr = htonl(INADDR_LOOPBACK) };
    if (setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one))
        || setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &one, sizeof(one))
        || bind(fd, (struct sockaddr *)&address, sizeof(address)) || listen(fd, 4096)) {
        int error = errno;
        close(fd);
        return -error;
    }
    return fd;
}

int ep_accept(int listener)
{
    int fd = accept4(listener, NULL, NULL, SOCK_CLOEXEC | SOCK_NONBLOCK);
    return fd < 0 ? -errno : fd;
}

int ep_socket_option(int fd, int option, int value)
{
    int level = option == 2 ? SOL_SOCKET : IPPROTO_TCP;
    int name = option == 0 ? TCP_NODELAY : option == 1 ? TCP_CORK : SO_SNDBUF;
    return setsockopt(fd, level, name, &value, sizeof(value)) ? -errno : 0;
}

int ep_shutdown(int fd) { return shutdown(fd, SHUT_RDWR) ? -errno : 0; }
int ep_close(int fd) { return close(fd) ? -errno : 0; }

int ep_eventfd(void)
{
    int fd = eventfd(0, EFD_CLOEXEC | EFD_NONBLOCK);
    return fd < 0 ? -errno : fd;
}

int ep_wake(int fd)
{
    uint64_t one = 1;
    ssize_t result;
    do { result = write(fd, &one, sizeof(one)); } while (result < 0 && errno == EINTR);
    return result == sizeof(one) || (result < 0 && errno == EAGAIN) ? 0 : -errno;
}

int ep_drain_wake(int fd)
{
    uint64_t value;
    ssize_t result;
    do { result = read(fd, &value, sizeof(value)); } while (result < 0 && errno == EINTR);
    return result == sizeof(value) || (result < 0 && errno == EAGAIN) ? 0 : -errno;
}

int ep_control(int epollfd, int operation, int fd, uint32_t events, uint64_t id)
{
    struct epoll_event event = { .events = events, .data.u64 = id };
    return epoll_ctl(epollfd, operation, fd, &event) ? -errno : 0;
}

int ep_wait(int epollfd, struct ready_event *output, int capacity, int timeout)
{
    struct epoll_event events[128];
    if (capacity < 1 || capacity > 128) return -EINVAL;
    int count = epoll_wait(epollfd, events, capacity, timeout);
    if (count < 0) return errno == EINTR ? 0 : -errno;
    for (int i = 0; i < count; i++)
        output[i] = (struct ready_event){ events[i].data.u64, events[i].events, 0 };
    return count;
}

SSL_CTX *ep_context(const char *cert, const char *key, uint64_t *error)
{
    ERR_clear_error();
    SSL_CTX *ctx = SSL_CTX_new(TLS_server_method());
    if (!ctx || !SSL_CTX_set_min_proto_version(ctx, TLS1_2_VERSION)
        || !SSL_CTX_set_max_proto_version(ctx, TLS1_2_VERSION)
        || !SSL_CTX_set_cipher_list(ctx, "ECDHE-RSA-AES128-GCM-SHA256")
        || !SSL_CTX_use_certificate_chain_file(ctx, cert)
        || !SSL_CTX_use_PrivateKey_file(ctx, key, SSL_FILETYPE_PEM)
        || !SSL_CTX_check_private_key(ctx)) {
        *error = ERR_peek_error();
        SSL_CTX_free(ctx);
        return NULL;
    }
    SSL_CTX_set_session_cache_mode(ctx, SSL_SESS_CACHE_OFF);
    SSL_CTX_set_options(ctx, SSL_OP_NO_TICKET | SSL_OP_NO_RENEGOTIATION);
    SSL_CTX_set_read_ahead(ctx, 1);
    SSL_CTX_set_mode(ctx, SSL_MODE_AUTO_RETRY);
    *error = 0;
    return ctx;
}

SSL *ep_session(SSL_CTX *ctx, int fd, uint64_t *error)
{
    ERR_clear_error();
    SSL *ssl = SSL_new(ctx);
    if (!ssl || !SSL_set_fd(ssl, fd)) {
        *error = ERR_peek_error();
        SSL_free(ssl);
        return NULL;
    }
    SSL_set_accept_state(ssl);
    *error = 0;
    return ssl;
}

// Keep SSL_get_error adjacent to its operation on the same OS thread.
void ep_tls(SSL *ssl, int operation, void *buffer, int length, struct tls_result *output)
{
    ERR_clear_error();
    errno = 0;
    size_t transferred = 0;
    int result;
    switch (operation) {
    case 0: result = SSL_do_handshake(ssl); break;
    case 1: result = SSL_read_ex(ssl, buffer, length, &transferred); break;
    case 2: result = SSL_write_ex(ssl, buffer, length, &transferred); break;
    case 3: result = SSL_shutdown(ssl); break;
    default: *output = (struct tls_result){ .status = FAILED, .error = EINVAL }; return;
    }
    int saved_errno = errno;
    *output = (struct tls_result){ .length = (int)transferred };
    if (result == 1 || (operation == 3 && result == 0)) {
        output->pending = SSL_has_pending(ssl);
        return;
    }
    int error = SSL_get_error(ssl, result);
    output->reason = ERR_peek_error();
    output->error = saved_errno;
    if (error == SSL_ERROR_WANT_READ) output->status = WANT_READ;
    else if (error == SSL_ERROR_WANT_WRITE) output->status = WANT_WRITE;
    else if (error == SSL_ERROR_ZERO_RETURN) output->status = CLOSED;
    else if ((error == SSL_ERROR_SYSCALL && (saved_errno == ECONNRESET || saved_errno == EPIPE))
        || (error == SSL_ERROR_SSL && ERR_GET_REASON(output->reason) == SSL_R_UNEXPECTED_EOF_WHILE_READING))
        output->status = PEER_ABORT;
    else output->status = FAILED;
}

void ep_free_session(SSL *ssl) { SSL_free(ssl); }
void ep_free_context(SSL_CTX *ctx) { SSL_CTX_free(ctx); }
const char *ep_version(void) { return OpenSSL_version(OPENSSL_VERSION); }
