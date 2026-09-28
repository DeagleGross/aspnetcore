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
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/eventfd.h>
#include <sys/socket.h>
#include <unistd.h>

/* Prototype ABI: all state mutation is confined to the pump thread. */
enum { PAGES = 2048, PAGE_SIZE = 16384, EVENTS = 8192 };
enum { ACCEPT, READ, WRITE, CANCEL, WAKE, FINAL_SHUTDOWN };
struct engine;
struct connection;
struct op { int kind; struct connection *c; };
struct event { int kind, result; uint64_t connection; void *data; int page, length; };
struct command { int kind, length; uint64_t connection; void *data; int page, unused; };
struct connection {
    struct engine *e;
    int fd, closing, ops, announced, done, receive_active, write_active, cancel_read, cancel_write;
    int leased, handshake, waiting_write, read_page;
    int fatal, close_requested, read_backpressured;
    SSL *ssl;
    const unsigned char *send_data;
    int send_length, send_offset, final_send, fin_active, fin_complete, corked;
    struct op read, write, cr, cw, fin;
    struct connection *next, *previous;
};
struct engine {
    struct io_uring ring;
    int listener, wakefd, tls, accept_active, stopping, error;
    SSL_CTX *ctx;
    struct io_uring_buf_ring *br;
    unsigned char *pages;
    int free_pages[PAGES], free_count;
    struct op accept, wake, ca;
    uint64_t wake_value;
    struct connection *connections;
    struct event events[EVENTS];
    int event_count;
    uint64_t accepts, receives, sends, polls, completions, enters, returns, tls_reads;
    int require_ktls;
    uint64_t handshakes, ktls_rx, ktls_tx, ktls_rejected;
    int guard_page_return;
    uint64_t page_read_drives, page_read_skips, tls_read_success, tls_want_read;
    uint64_t final_send_shutdowns, send_partials;
    int linked_final_send, final_more;
    uint64_t final_links, cancelled_links, shutdown_errors, shutdown_after_close, shutdown_not_connected;
    uint64_t tls_final_shutdowns, tls_close_notify, tls_shutdown_failures, tls_corks;
};

static void emit(struct engine *e, int kind, struct connection *c, int result, int page, int length)
{
    if (e->event_count == EVENTS) abort();
    e->events[e->event_count++] = (struct event) {
        kind, result, (uint64_t)(uintptr_t)c,
        page >= 0 ? e->pages + (size_t)page * PAGE_SIZE : NULL, page, length
    };
}
static struct io_uring_sqe *sqe(struct engine *e)
{
    struct io_uring_sqe *s = io_uring_get_sqe(&e->ring);
    if (!s) {
        int r = io_uring_submit(&e->ring);
        e->enters++;
        if (r < 0) abort();
        s = io_uring_get_sqe(&e->ring);
    }
    if (!s) abort();
    return s;
}
static void offer(struct engine *e, int page)
{
    if (e->tls) e->free_pages[e->free_count++] = page;
    else {
        io_uring_buf_ring_add(e->br, e->pages + (size_t)page * PAGE_SIZE, PAGE_SIZE, page, PAGES - 1, 0);
        io_uring_buf_ring_advance(e->br, 1);
    }
}
static void done(struct connection *c)
{
    if (c->closing && !c->ops && !c->done) {
        c->done = 1;
        if (c->read_page >= 0) { offer(c->e, c->read_page); c->read_page = -1; }
        emit(c->e, 5, c, 0, -1, 0);
    }
}
static void cancel(struct connection *c, struct op *target, struct op *result)
{
    struct io_uring_sqe *s = sqe(c->e);
    io_uring_prep_cancel(s, target, 0);
    io_uring_sqe_set_data(s, result);
    c->ops++;
}
static void write_start(struct connection *c);
static void close_connection(struct connection *c)
{
    if (!c->closing) {
        c->close_requested = 1;
        /* The managed send continuation can request close before the linked
         * shutdown CQE arrives. Do not race a second shutdown against it. */
        if (c->fin_active && !c->send_data) return;
        if (c->ssl && !c->send_data && c->handshake && !c->fatal) {
            ERR_clear_error();
            int result = SSL_shutdown(c->ssl);
            if (result < 0) {
                int error = SSL_get_error(c->ssl, result);
                if (error == SSL_ERROR_WANT_WRITE) {
                    c->waiting_write = 1;
                    write_start(c);
                    return;
                }
                /* Do not retry TLS after a fatal error. Report failed alerts. */
                if (c->final_send) c->e->tls_shutdown_failures++;
            } else if (c->final_send) {
                c->e->tls_close_notify++;
            }
        }
        c->closing = 1;
        int shutdown_result = shutdown(c->fd, SHUT_RDWR);
        if (c->ssl && c->final_send) {
            if (shutdown_result == 0) c->e->tls_final_shutdowns++;
            else if (errno == ENOTCONN) c->e->shutdown_not_connected++;
            else {
                c->e->shutdown_errors++;
                fprintf(stderr, "TLS final shutdown failed: %s\n", strerror(errno));
            }
        }
        if (c->receive_active && !c->cancel_read) {
            c->cancel_read = 1; cancel(c, &c->read, &c->cr);
        }
        if (c->write_active && !c->cancel_write) {
            c->cancel_write = 1; cancel(c, &c->write, &c->cw);
        }
    }
    done(c);
}
static void read_start(struct connection *c)
{
    if (c->ssl && c->leased >= 4) {
        /* SSL_has_pending can be false with unread records still in the socket. */
        c->read_backpressured = 1;
    }
    if (c->closing || c->receive_active || c->cancel_read || c->leased >= 4) return;
    struct io_uring_sqe *s = sqe(c->e);
    if (c->ssl) {
        io_uring_prep_poll_multishot(s, c->fd, POLLIN);
        c->e->polls++;
    } else {
        io_uring_prep_recv_multishot(s, c->fd, NULL, 0, 0);
        s->flags |= IOSQE_BUFFER_SELECT;
        s->buf_group = 1;
        c->e->receives++;
    }
    io_uring_sqe_set_data(s, &c->read);
    c->receive_active = 1; c->ops++;
}
static void write_start(struct connection *c)
{
    if (c->closing || c->write_active) return;
    int linked = !c->ssl && c->final_send && c->e->linked_final_send;
    if (linked && io_uring_sq_space_left(&c->e->ring) < 2) {
        /* Do not submit a link head without its successor in the same batch. */
        int result = io_uring_submit(&c->e->ring);
        c->e->enters++;
        if (result < 0 || io_uring_sq_space_left(&c->e->ring) < 2) abort();
    }
    struct io_uring_sqe *s = sqe(c->e);
    if (c->ssl) {
        io_uring_prep_poll_add(s, c->fd, POLLOUT);
        c->e->polls++;
    } else {
        /* MSG_WAITALL makes a short linked send fail the chain, cancelling
         * shutdown. MSG_MORE defers the known-final tail's push until FIN. */
        io_uring_prep_send(s, c->fd, c->send_data + c->send_offset,
            c->send_length - c->send_offset,
            MSG_NOSIGNAL | (linked ? MSG_WAITALL : 0) | (linked && c->e->final_more ? MSG_MORE : 0));
        if (linked) s->flags |= IOSQE_IO_LINK;
        c->e->sends++;
    }
    io_uring_sqe_set_data(s, &c->write);
    c->write_active = 1; c->ops++;
    if (linked) {
        struct io_uring_sqe *shutdown_sqe = sqe(c->e);
        io_uring_prep_shutdown(shutdown_sqe, c->fd, SHUT_WR);
        io_uring_sqe_set_data(shutdown_sqe, &c->fin);
        c->fin_active = 1; c->ops++; c->e->final_links++;
    }
}
static int ssl_result(struct connection *c, int result)
{
    if (result == 1) return 1;
    int error = SSL_get_error(c->ssl, result);
    if (error == SSL_ERROR_WANT_READ) { c->e->tls_want_read++; read_start(c); return 0; }
    if (error == SSL_ERROR_WANT_WRITE) { c->waiting_write = 1; write_start(c); return 0; }
    emit(c->e, 3, c, error == SSL_ERROR_ZERO_RETURN ? 0 : -EPROTO, -1, 0);
    c->fatal = error != SSL_ERROR_ZERO_RETURN;
    close_connection(c);
    return -1;
}
static void tls_drive(struct connection *c)
{
    if (c->closing || c->waiting_write) return;
    if (c->close_requested) { close_connection(c); return; }
    if (!c->handshake) {
        ERR_clear_error();
        int r = SSL_do_handshake(c->ssl);
        if (ssl_result(c, r) != 1) return;
        c->handshake = 1;
        int rx = BIO_get_ktls_recv(SSL_get_rbio(c->ssl));
        int tx = BIO_get_ktls_send(SSL_get_wbio(c->ssl));
        c->e->handshakes++;
        c->e->ktls_rx += rx != 0;
        c->e->ktls_tx += tx != 0;
        if (c->e->handshakes == 1)
            fprintf(stdout, "KTLS_ACTIVATION mode=fd required=%d rx=%d tx=%d\n", c->e->require_ktls, rx, tx);
        if (c->e->require_ktls && (!rx || !tx)) {
            c->e->ktls_rejected++;
            fprintf(stderr, "kTLS required but not active: fd rx=%d tx=%d\n", rx, tx);
            c->fatal = 1;
            close_connection(c);
            return;
        }
        c->announced = 1;
        emit(c->e, 1, c, 0, -1, 0);
    }
    if (c->send_data) {
        size_t count = 0;
        ERR_clear_error();
        int r = SSL_write_ex(c->ssl, c->send_data + c->send_offset,
            c->send_length - c->send_offset, &count);
        if (ssl_result(c, r) != 1) return;
        c->send_offset += (int)count;
        if (c->send_offset != c->send_length) { tls_drive(c); return; }
        c->send_data = NULL;
        emit(c->e, 4, c, c->send_length, -1, 0);
        if (c->final_send) {
            close_connection(c); /* SSL close_notify before transport FIN. */
            return;
        }
        /* No speculative empty SSL_read following each response. */
        if (!SSL_has_pending(c->ssl)) { read_start(c); return; }
    }
    c->read_backpressured = 0;
    while (!c->closing && c->leased < 4) {
        if (c->read_page < 0) {
            if (!c->e->free_count) { c->read_backpressured = 1; return; }
            c->read_page = c->e->free_pages[--c->e->free_count];
        }
        size_t count = 0;
        ERR_clear_error();
        int r = SSL_read_ex(c->ssl, c->e->pages + (size_t)c->read_page * PAGE_SIZE, PAGE_SIZE, &count);
        c->e->tls_reads++;
        if (ssl_result(c, r) != 1) return;
        c->e->tls_read_success++;
        c->leased++;
        emit(c->e, 2, c, 0, c->read_page, (int)count);
        c->read_page = -1;
        if (!SSL_has_pending(c->ssl)) { read_start(c); return; }
    }
    if (!c->closing && c->leased >= 4) c->read_backpressured = 1;
}
static void free_connection(struct connection *c)
{
    if (!c->done || c->leased) abort();
    if (c->previous) c->previous->next = c->next;
    else c->e->connections = c->next;
    if (c->next) c->next->previous = c->previous;
    SSL_free(c->ssl); close(c->fd); free(c);
}
static void accept_start(struct engine *e)
{
    struct io_uring_sqe *s = sqe(e);
    io_uring_prep_multishot_accept(s, e->listener, NULL, NULL,
        SOCK_CLOEXEC | (e->tls ? SOCK_NONBLOCK : 0));
    io_uring_sqe_set_data(s, &e->accept);
    e->accept_active = 1; e->accepts++;
}
static void wake_start(struct engine *e)
{
    struct io_uring_sqe *s = sqe(e);
    io_uring_prep_read(s, e->wakefd, &e->wake_value, sizeof(e->wake_value), 0);
    io_uring_sqe_set_data(s, &e->wake);
}
void *np2_create(int port, int tls, const char *cert, const char *key, int cpu, int flags, int *error)
{
    struct engine *e = calloc(1, sizeof(*e));
    if (!e) { *error = ENOMEM; return NULL; }
    e->listener = e->wakefd = -1;
    cpu_set_t set; CPU_ZERO(&set); CPU_SET(cpu, &set);
    if (sched_setaffinity(0, sizeof(set), &set)) goto failure;
    int r = io_uring_queue_init(2048, &e->ring, IORING_SETUP_SINGLE_ISSUER |
        (flags ? IORING_SETUP_DEFER_TASKRUN : 0));
    if (r < 0) { errno = -r; goto failure; }
    e->tls = tls;
    const char *final_mode = getenv("NETWORKPROTO_FINAL_SEND");
    e->linked_final_send = final_mode && (!strcmp(final_mode, "2") || !strcmp(final_mode, "3"));
    e->final_more = final_mode && !strcmp(final_mode, "3");
    e->guard_page_return = getenv("NETWORKPROTO_GUARD_PAGE_READ") && !strcmp(getenv("NETWORKPROTO_GUARD_PAGE_READ"), "1");
    e->require_ktls = getenv("NETWORKPROTO_KTLS") && !strcmp(getenv("NETWORKPROTO_KTLS"), "1");
    if (e->require_ktls && !tls) { errno = EINVAL; goto failure_ring; }
    if (tls) {
        fprintf(stdout, "TLS_LIBRARY version=\"%s\" guard_page_return=%d require_ktls=%d\n",
            OpenSSL_version(OPENSSL_VERSION), e->guard_page_return, e->require_ktls);
        fflush(stdout);
    }
    if (posix_memalign((void **)&e->pages, 4096, (size_t)PAGES * PAGE_SIZE)) { errno = ENOMEM; goto failure_ring; }
    if (!tls) {
        e->br = io_uring_setup_buf_ring(&e->ring, PAGES, 1, 0, &r);
        if (!e->br) { errno = -r; goto failure_ring; }
    } else {
        e->ctx = SSL_CTX_new(TLS_server_method());
        if (!e->ctx || !SSL_CTX_set_min_proto_version(e->ctx, TLS1_2_VERSION)
            || !SSL_CTX_set_max_proto_version(e->ctx, TLS1_2_VERSION)
            || !SSL_CTX_set_cipher_list(e->ctx, "ECDHE-RSA-AES128-GCM-SHA256")
            || !SSL_CTX_use_certificate_chain_file(e->ctx, cert)
            || !SSL_CTX_use_PrivateKey_file(e->ctx, key, SSL_FILETYPE_PEM)
            || !SSL_CTX_check_private_key(e->ctx)) { errno = EPROTO; goto failure_ring; }
        SSL_CTX_set_session_cache_mode(e->ctx, SSL_SESS_CACHE_OFF);
        SSL_CTX_set_options(e->ctx, SSL_OP_NO_TICKET);
        SSL_CTX_set_read_ahead(e->ctx, 1);
        SSL_CTX_set_mode(e->ctx, SSL_MODE_AUTO_RETRY);
        if (e->require_ktls)
            SSL_CTX_set_options(e->ctx, SSL_OP_ENABLE_KTLS);
    }
    for (int i = 0; i < PAGES; i++) offer(e, i);
    e->listener = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    int one = 1;
    if (e->listener < 0 || setsockopt(e->listener, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one))
        || setsockopt(e->listener, SOL_SOCKET, SO_REUSEPORT, &one, sizeof(one))) goto failure_ring;
    struct sockaddr_in a = { .sin_family = AF_INET, .sin_port = htons(port), .sin_addr.s_addr = htonl(INADDR_LOOPBACK) };
    if (bind(e->listener, (struct sockaddr *)&a, sizeof(a)) || listen(e->listener, 4096)) goto failure_ring;
    e->wakefd = eventfd(0, EFD_CLOEXEC);
    if (e->wakefd < 0) goto failure_ring;
    e->accept = (struct op){ ACCEPT, NULL };
    e->wake = (struct op){ WAKE, NULL };
    e->ca = (struct op){ CANCEL, NULL };
    accept_start(e); wake_start(e);
    *error = 0; return e;
failure_ring:
    *error = errno;
    io_uring_queue_exit(&e->ring);
    if (e->listener >= 0) close(e->listener);
    if (e->wakefd >= 0) close(e->wakefd);
    SSL_CTX_free(e->ctx); free(e->pages); free(e);
    return NULL;
failure:
    *error = errno; free(e); return NULL;
}
int np2_wake(void *handle)
{
    struct engine *e = handle;
    uint64_t one = 1;
    return write(e->wakefd, &one, sizeof(one)) == sizeof(one) ? 0 : -errno;
}
int np2_step(void *handle, struct command *commands, int count, struct event **output)
{
    struct engine *e = handle;
    e->event_count = 0;
    for (int i = 0; i < count; i++) {
        struct command *cmd = &commands[i];
        struct connection *c = (void *)(uintptr_t)cmd->connection;
        if (cmd->kind == 1) {
            if (c->closing) emit(e, 4, c, -ECANCELED, -1, 0);
            else {
                if (c->send_data) abort();
                c->send_data = cmd->data; c->send_length = cmd->length; c->send_offset = 0;
                c->final_send = cmd->unused == 1;
                if (e->tls && c->final_send && e->final_more && !c->corked) {
                    int one = 1;
                    if (setsockopt(c->fd, IPPROTO_TCP, TCP_CORK, &one, sizeof(one)) != 0) {
                        int error = -errno;
                        c->fatal = 1; c->send_data = NULL;
                        emit(e, 4, c, error, -1, 0); close_connection(c);
                        continue;
                    }
                    c->corked = 1; e->tls_corks++;
                }
                if (e->tls) tls_drive(c); else write_start(c);
            }
        } else if (cmd->kind == 2) {
            offer(e, cmd->page); e->returns++; c->leased--;
            if (!c->closing) {
                if (!e->tls) read_start(c);
                else if (!e->guard_page_return || !c->handshake || c->close_requested || c->send_data
                    || c->read_backpressured || SSL_has_pending(c->ssl)) {
                    e->page_read_drives++;
                    tls_drive(c);
                } else {
                    /* Returning consumed memory is not a socket-readiness event.
                     * A paused reader or buffered TLS input must still progress. */
                    e->page_read_skips++;
                    read_start(c);
                }
            }
        } else if (cmd->kind == 3) close_connection(c);
        else if (cmd->kind == 4) free_connection(c);
        else if (cmd->kind == 5 && !e->stopping) {
            e->stopping = 1;
            struct io_uring_sqe *s = sqe(e);
            io_uring_prep_cancel(s, &e->accept, 0); io_uring_sqe_set_data(s, &e->ca);
        }
    }
    int r = io_uring_submit(&e->ring);
    e->enters++;
    if (r < 0) return r;
    if (!e->event_count) {
        struct io_uring_cqe *cqe;
        struct __kernel_timespec timeout = { .tv_sec = 0, .tv_nsec = 10000000 };
        r = io_uring_wait_cqe_timeout(&e->ring, &cqe, &timeout);
        e->enters++;
        if (r < 0 && r != -ETIME && r != -EINTR) return r;
    }
    struct io_uring_cqe *cqe;
    unsigned head, consumed = 0;
    io_uring_for_each_cqe(&e->ring, head, cqe) {
        if (e->event_count > EVENTS - 512) break;
        struct op *op = io_uring_cqe_get_data(cqe);
        struct connection *c = op->c;
        int res = cqe->res, terminal = !(cqe->flags & IORING_CQE_F_MORE);
        e->completions++; consumed++;
        if (op->kind == WAKE) { wake_start(e); continue; }
        if (op->kind == ACCEPT) {
            if (terminal) e->accept_active = 0;
            if (res >= 0) {
                if (e->stopping) close(res);
                else {
                    c = calloc(1, sizeof(*c));
                    if (!c) abort();
                    c->e = e; c->fd = res; c->read_page = -1;
                    c->read = (struct op){ READ, c }; c->write = (struct op){ WRITE, c };
                    c->cr = (struct op){ CANCEL, c }; c->cw = (struct op){ CANCEL, c };
                    c->fin = (struct op){ FINAL_SHUTDOWN, c };
                    c->next = e->connections;
                    if (c->next) c->next->previous = c;
                    e->connections = c;
                    int one = 1; setsockopt(res, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(one));
                    if (e->tls) {
                        c->ssl = SSL_new(e->ctx);
                        if (!c->ssl || !SSL_set_fd(c->ssl, res)) abort();
                        SSL_set_accept_state(c->ssl); tls_drive(c);
                    } else {
                        c->announced = 1; emit(e, 1, c, 0, -1, 0); read_start(c);
                    }
                }
            } else if (res != -ECANCELED) emit(e, 6, NULL, res, -1, 0);
            if (!e->accept_active && !e->stopping) accept_start(e);
        } else if (op->kind == CANCEL) {
            if (c) {
                c->ops--;
                if (op == &c->cr) c->cancel_read = 0; else c->cancel_write = 0;
                if (!c->closing) read_start(c);
                done(c);
            }
        } else if (op->kind == READ) {
            if (terminal) { c->receive_active = 0; c->ops--; }
            if (!e->tls && (cqe->flags & IORING_CQE_F_BUFFER)) {
                int page = cqe->flags >> IORING_CQE_BUFFER_SHIFT;
                if (res > 0 && !c->closing) { c->leased++; emit(e, 2, c, 0, page, res); }
                else offer(e, page);
            }
            if (!c->closing) {
                if (res < 0 && res != -ECANCELED && res != -ENOBUFS) {
                    c->fatal = 1; emit(e, 3, c, res, -1, 0); close_connection(c);
                } else if (!e->tls && res == 0) {
                    emit(e, 3, c, 0, -1, 0);
                } else if (e->tls && res >= 0) tls_drive(c);
                else read_start(c);
                if (!e->tls && c->leased >= 4 && c->receive_active && !c->cancel_read) {
                    c->cancel_read = 1; cancel(c, &c->read, &c->cr);
                }
            }
            done(c);
        } else if (op->kind == FINAL_SHUTDOWN) {
            c->fin_active = 0; c->ops--;
            if (res == 0) {
                e->final_send_shutdowns++;
                c->fin_complete = 1;
                if (!c->closing && !c->write_active && !c->send_data) close_connection(c);
            } else if (res == -ECANCELED) {
                e->cancelled_links++;
                if (!c->closing && c->send_data && !c->write_active)
                    write_start(c);
            } else if (!c->closing) {
                if (res == -ENOTCONN) {
                    e->shutdown_not_connected++;
                    if (e->shutdown_not_connected <= 4)
                        fprintf(stderr, "final-send shutdown after peer disconnected: %s\n", strerror(-res));
                } else {
                    e->shutdown_errors++;
                    if (e->shutdown_errors <= 4)
                        fprintf(stderr, "final-send shutdown error: %s\n", strerror(-res));
                }
                emit(e, 3, c, res, -1, 0);
                close_connection(c);
            } else {
                e->shutdown_after_close++;
            }
            done(c);
        } else if (op->kind == WRITE) {
            c->write_active = 0; c->ops--;
            if (e->tls) {
                c->waiting_write = 0;
                if (!c->closing) tls_drive(c);
            } else if (c->send_data) {
                if (res > 0 && !c->closing) {
                    c->send_offset += res;
                    if (c->send_offset < c->send_length) {
                        e->send_partials++;
                        if (!c->fin_active) write_start(c);
                    }
                    else {
                        c->send_data = NULL;
                        emit(e, 4, c, c->send_length, -1, 0);
                        if (c->final_send && (!e->linked_final_send || c->fin_complete)) {
                            if (!e->linked_final_send) e->final_send_shutdowns++;
                            close_connection(c);
                        }
                    }
                } else { c->send_data = NULL; emit(e, 4, c, res < 0 ? res : -EPIPE, -1, 0); }
            }
            done(c);
        }
    }
    io_uring_cq_advance(&e->ring, consumed);
    *output = e->events;
    return e->event_count;
}
void np2_stats(void *handle, uint64_t *out)
{
    struct engine *e = handle;
    uint64_t values[] = {e->accepts,e->receives,e->sends,e->polls,e->completions,e->enters,e->returns,e->tls_reads};
    memcpy(out, values, sizeof(values));
}
void np2_destroy(void *handle)
{
    struct engine *e = handle;
    if (e->connections) abort();
    fprintf(stdout, "KTLS_METRICS {\"mode\":\"fd\",\"required\":%d,\"handshakes\":%lu,\"rx\":%lu,\"tx\":%lu,\"rejected\":%lu}\n",
        e->require_ktls, e->handshakes, e->ktls_rx, e->ktls_tx, e->ktls_rejected);
    fprintf(stdout, "TLS_READ_METRICS {\"guard\":%d,\"pageDrives\":%lu,\"pageSkips\":%lu,\"sslReads\":%lu,\"successfulReads\":%lu,\"wantReadAllOperations\":%lu}\n",
        e->guard_page_return, e->page_read_drives, e->page_read_skips, e->tls_reads, e->tls_read_success, e->tls_want_read);
    fflush(stdout);
    fprintf(stdout, "FINAL_SEND_METRICS {\"shutdowns\":%lu,\"partialSends\":%lu,\"links\":%lu,\"cancelledLinks\":%lu,\"shutdownErrors\":%lu,\"shutdownAfterClose\":%lu,\"shutdownNotConnected\":%lu,\"tlsFinalShutdowns\":%lu,\"tlsCloseNotify\":%lu,\"tlsShutdownFailures\":%lu,\"tlsCorks\":%lu}\n",
        e->final_send_shutdowns, e->send_partials, e->final_links, e->cancelled_links, e->shutdown_errors, e->shutdown_after_close, e->shutdown_not_connected,
        e->tls_final_shutdowns, e->tls_close_notify, e->tls_shutdown_failures, e->tls_corks);
    fflush(stdout);
    io_uring_queue_exit(&e->ring);
    close(e->listener); close(e->wakefd);
    SSL_CTX_free(e->ctx); free(e->pages); free(e);
}
