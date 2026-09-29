// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <liburing.h>
#include <netinet/tcp.h>
#include <openssl/err.h>
#include <openssl/ssl.h>
#include <poll.h>
#include <sched.h>
#include <stdint.h>
#include <stdlib.h>
#include <sys/eventfd.h>
#include <unistd.h>

// Opaque liburing ABI only. Connections, operation identities and buffers belong to C#.
struct ring { struct io_uring ring; struct io_uring_buf_ring *buffers; };
struct completion { uint64_t id; int result; unsigned flags; };
struct tls_result { int status, length, pending, error; uint64_t reason; };

void *ur_create(int defer, int buffers, int *error)
{
    struct ring *r = calloc(1, sizeof(*r));
    if (!r) { *error = ENOMEM; return NULL; }
    int result = io_uring_queue_init(2048, &r->ring, IORING_SETUP_SINGLE_ISSUER | (defer ? IORING_SETUP_DEFER_TASKRUN : 0));
    if (result < 0) { *error = -result; free(r); return NULL; }
    if (buffers) {
        r->buffers = io_uring_setup_buf_ring(&r->ring, 2048, 1, 0, &result);
        if (!r->buffers) { *error = -result; io_uring_queue_exit(&r->ring); free(r); return NULL; }
    }
    *error = 0;
    return r;
}

int ur_prepare(struct ring *r, int kind, int fd, void *data, int length, uint64_t id, uint64_t target, int flags)
{
    int needed = kind == 7 ? 2 : 1;
    if (io_uring_sq_space_left(&r->ring) < (unsigned)needed) {
        int result = io_uring_submit(&r->ring);
        if (result < 0) return result;
        if (io_uring_sq_space_left(&r->ring) < (unsigned)needed) return -EAGAIN;
    }
    struct io_uring_sqe *sqe = io_uring_get_sqe(&r->ring);
    switch (kind) {
    case 0: io_uring_prep_multishot_accept(sqe, fd, NULL, NULL, SOCK_CLOEXEC | (flags ? SOCK_NONBLOCK : 0)); break;
    case 1: io_uring_prep_poll_multishot(sqe, fd, POLLIN); break;
    case 2:
        io_uring_prep_recv_multishot(sqe, fd, NULL, 0, 0);
        sqe->flags |= IOSQE_BUFFER_SELECT; sqe->buf_group = 1;
        break;
    case 3: io_uring_prep_poll_add(sqe, fd, POLLOUT); break;
    case 4: io_uring_prep_send(sqe, fd, data, length, MSG_NOSIGNAL); break;
    case 5: io_uring_prep_cancel64(sqe, target, 0); break;
    case 6: io_uring_prep_read(sqe, fd, data, length, 0); break;
    case 7:
        io_uring_prep_send(sqe, fd, data, length, MSG_NOSIGNAL | MSG_WAITALL | (flags ? MSG_MORE : 0));
        sqe->flags |= IOSQE_IO_LINK;
        break;
    default: return -EINVAL;
    }
    io_uring_sqe_set_data64(sqe, id);
    if (kind == 7) {
        sqe = io_uring_get_sqe(&r->ring);
        io_uring_prep_shutdown(sqe, fd, SHUT_WR);
        io_uring_sqe_set_data64(sqe, target);
    }
    return 0;
}

int ur_collect(struct ring *r, struct completion *output, int capacity, int wait)
{
    int result = io_uring_submit(&r->ring);
    if (result < 0) return result;
    if (wait && !io_uring_cq_ready(&r->ring)) {
        struct io_uring_cqe *cqe;
        struct __kernel_timespec timeout = { .tv_nsec = 10000000 };
        result = io_uring_wait_cqe_timeout(&r->ring, &cqe, &timeout);
        if (result < 0 && result != -ETIME && result != -EINTR) return result;
    }
    unsigned head, count = 0;
    struct io_uring_cqe *cqe;
    io_uring_for_each_cqe(&r->ring, head, cqe) {
        if (count == (unsigned)capacity) break;
        output[count++] = (struct completion){ cqe->user_data, cqe->res, cqe->flags };
    }
    io_uring_cq_advance(&r->ring, count);
    return (int)count;
}

void ur_offer(struct ring *r, void *data, int length, int page)
{
    io_uring_buf_ring_add(r->buffers, data, length, page, 2047, 0);
    io_uring_buf_ring_advance(r->buffers, 1);
}

void ur_destroy(struct ring *r) { io_uring_queue_exit(&r->ring); free(r); }

int ur_cpu(int cpu)
{
    if (cpu < 0 || cpu >= CPU_SETSIZE) return -EINVAL;
    cpu_set_t set; CPU_ZERO(&set); CPU_SET(cpu, &set);
    return sched_setaffinity(0, sizeof(set), &set) ? -errno : 0;
}

int ur_listener(int port)
{
    int fd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (fd < 0) return -errno;
    int one = 1;
    struct sockaddr_in address = { .sin_family = AF_INET, .sin_port = htons(port), .sin_addr.s_addr = htonl(INADDR_LOOPBACK) };
    if (setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one))
        || setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &one, sizeof(one))
        || bind(fd, (struct sockaddr *)&address, sizeof(address)) || listen(fd, 4096)) {
        int error = errno; close(fd); return -error;
    }
    return fd;
}

int ur_option(int fd, int option, int value)
{
    return setsockopt(fd, option == 2 ? SOL_SOCKET : IPPROTO_TCP,
        option == 0 ? TCP_NODELAY : option == 1 ? TCP_CORK : SO_SNDBUF, &value, sizeof(value)) ? -errno : 0;
}
int ur_shutdown(int fd) { return shutdown(fd, SHUT_RDWR) ? -errno : 0; }
int ur_close(int fd) { return close(fd) ? -errno : 0; }
int ur_eventfd(void) { int fd = eventfd(0, EFD_CLOEXEC); return fd < 0 ? -errno : fd; }
int ur_wake(int fd)
{
    uint64_t one = 1;
    ssize_t result;
    do { result = write(fd, &one, sizeof(one)); } while (result < 0 && errno == EINTR);
    return result == sizeof(one) ? 0 : -errno;
}

SSL_CTX *ur_context(const char *cert, const char *key, int ktls, uint64_t *error)
{
    ERR_clear_error();
    SSL_CTX *ctx = SSL_CTX_new(TLS_server_method());
    if (!ctx || !SSL_CTX_set_min_proto_version(ctx, TLS1_2_VERSION)
        || !SSL_CTX_set_max_proto_version(ctx, TLS1_2_VERSION)
        || !SSL_CTX_set_cipher_list(ctx, "ECDHE-RSA-AES128-GCM-SHA256")
        || !SSL_CTX_use_certificate_chain_file(ctx, cert)
        || !SSL_CTX_use_PrivateKey_file(ctx, key, SSL_FILETYPE_PEM)
        || !SSL_CTX_check_private_key(ctx)) {
        *error = ERR_peek_error(); SSL_CTX_free(ctx); return NULL;
    }
    SSL_CTX_set_session_cache_mode(ctx, SSL_SESS_CACHE_OFF);
    SSL_CTX_set_options(ctx, SSL_OP_NO_TICKET | (ktls ? SSL_OP_ENABLE_KTLS : 0));
    SSL_CTX_set_read_ahead(ctx, 1);
    SSL_CTX_set_mode(ctx, SSL_MODE_AUTO_RETRY);
    *error = 0; return ctx;
}
SSL *ur_session(SSL_CTX *ctx, int fd)
{
    SSL *ssl = SSL_new(ctx);
    if (!ssl || !SSL_set_fd(ssl, fd)) { SSL_free(ssl); return NULL; }
    SSL_set_accept_state(ssl);
    return ssl;
}
int ur_ktls(SSL *ssl) { return (BIO_get_ktls_recv(SSL_get_rbio(ssl)) ? 1 : 0) | (BIO_get_ktls_send(SSL_get_wbio(ssl)) ? 2 : 0); }
void ur_tls(SSL *ssl, int operation, void *buffer, int length, struct tls_result *output)
{
    ERR_clear_error(); errno = 0;
    size_t count = 0;
    int result;
    switch (operation) {
    case 0: result = SSL_do_handshake(ssl); break;
    case 1: result = SSL_read_ex(ssl, buffer, length, &count); break;
    case 2: result = SSL_write_ex(ssl, buffer, length, &count); break;
    case 3: result = SSL_shutdown(ssl); break;
    default: *output = (struct tls_result){ .status = 5, .error = EINVAL }; return;
    }
    int saved_errno = errno;
    *output = (struct tls_result){ .length = (int)count };
    if (result == 1 || (operation == 3 && result == 0)) {
        output->pending = SSL_has_pending(ssl); return;
    }
    int error = SSL_get_error(ssl, result);
    output->error = saved_errno; output->reason = ERR_peek_error();
    if (error == SSL_ERROR_WANT_READ) output->status = 1;
    else if (error == SSL_ERROR_WANT_WRITE) output->status = 2;
    else if (error == SSL_ERROR_ZERO_RETURN) output->status = 3;
    else if ((error == SSL_ERROR_SYSCALL && (saved_errno == ECONNRESET || saved_errno == EPIPE))
        || (error == SSL_ERROR_SSL && ERR_GET_REASON(output->reason) == SSL_R_UNEXPECTED_EOF_WHILE_READING)) output->status = 4;
    else output->status = 5;
}
void ur_free_session(SSL *ssl) { SSL_free(ssl); }
void ur_free_context(SSL_CTX *ctx) { SSL_CTX_free(ctx); }
const char *ur_version(void) { return OpenSSL_version(OPENSSL_VERSION); }
