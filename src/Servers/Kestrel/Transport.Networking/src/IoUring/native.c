// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#define _GNU_SOURCE
#include <liburing.h>
#include <sys/eventfd.h>
#include <sys/socket.h>
#include <unistd.h>
#include <poll.h>
#include <errno.h>
#include <stdint.h>
#include <stdlib.h>
#include <sys/syscall.h>

struct np_ring
{
    struct io_uring ring;
    int wake;
    int rearm_wake;
};

// The legacy and pump-staging paths own the SQ on the pump. Direct staging
// serializes SQ access with the managed gate. CQ access is always pump-only.
static struct io_uring_sqe *get_sqe(struct np_ring *p)
{
    struct io_uring_sqe *sqe = io_uring_get_sqe(&p->ring);
    if (!sqe)
    {
        int result = io_uring_submit(&p->ring);
        if (result < 0)
        {
            errno = -result;
            return NULL;
        }
        sqe = io_uring_get_sqe(&p->ring);
        if (!sqe)
        {
            errno = EBUSY;
        }
    }
    return sqe;
}

static int arm_wake(struct np_ring *p)
{
    struct io_uring_sqe *sqe = get_sqe(p);
    if (!sqe)
    {
        return -errno;
    }
    io_uring_prep_poll_add(sqe, p->wake, POLLIN);
    io_uring_sqe_set_data64(sqe, 0);
    return 0;
}

struct np_ring *np_create_configured(unsigned entries, int deferred, int *error)
{
    struct np_ring *p = calloc(1, sizeof(*p));
    if (!p)
    {
        *error = ENOMEM;
        return NULL;
    }
    unsigned flags = deferred
        ? IORING_SETUP_R_DISABLED | IORING_SETUP_SINGLE_ISSUER | IORING_SETUP_DEFER_TASKRUN | IORING_SETUP_TASKRUN_FLAG
        : 0;
    int result = io_uring_queue_init(entries, &p->ring, flags);
    if (result < 0)
    {
        *error = -result;
        free(p);
        return NULL;
    }
    p->wake = eventfd(0, EFD_CLOEXEC | EFD_NONBLOCK);
    if (p->wake < 0)
    {
        *error = errno;
        io_uring_queue_exit(&p->ring);
        free(p);
        return NULL;
    }
    *error = -arm_wake(p);
    if (*error)
    {
        close(p->wake);
        io_uring_queue_exit(&p->ring);
        free(p);
        return NULL;
    }
    return p;
}

struct np_ring *np_create_size(unsigned entries, int *error)
{
    return np_create_configured(entries, 0, error);
}

int np_enable(struct np_ring *p)
{
    // With R_DISABLED, the enabling thread becomes the single issuer.
    return p->ring.flags & IORING_SETUP_R_DISABLED ? io_uring_enable_rings(&p->ring) : 0;
}

struct np_ring *np_create(int *error)
{
    return np_create_size(1024, error);
}

// Caller serializes SQ preparation and submission. Zero means SQ full, not EAGAIN from I/O.
int np_stage(struct np_ring *p, int operation, int fd, void *buffer, unsigned length, uint64_t id)
{
    struct io_uring_sqe *sqe = io_uring_get_sqe(&p->ring);
    if (!sqe)
    {
        return 0;
    }
    switch (operation)
    {
        case 0:
            io_uring_prep_accept(sqe, fd, NULL, NULL, SOCK_CLOEXEC);
            break;
        case 1:
            io_uring_prep_recv(sqe, fd, buffer, length, 0);
            break;
        case 2:
            io_uring_prep_send(sqe, fd, buffer, length, MSG_NOSIGNAL);
            break;
        case 3:
            io_uring_prep_cancel64(sqe, id, 0);
            io_uring_sqe_set_data64(sqe, 1);
            return 1;
        case 4:
            io_uring_prep_multishot_accept(sqe, fd, NULL, NULL, SOCK_CLOEXEC);
            break;
        default:
            abort();
    }
    io_uring_sqe_set_data64(sqe, id);
    return 1;
}

static int rearm_wake(struct np_ring *p)
{
    if (p->rearm_wake)
    {
        int result = arm_wake(p);
        if (result < 0)
        {
            return result;
        }
        p->rearm_wake = 0;
    }
    return 0;
}

int np_flush(struct np_ring *p)
{
    int result = rearm_wake(p);
    return result < 0 ? result : io_uring_submit(&p->ring);
}

// The pump is the sole CQ consumer. Do not use a wait helper that can flush the
// SQ here: producers may be preparing SQEs while the pump sleeps.
int np_collect(struct np_ring *p, uint64_t *ids, int *results, unsigned capacity, int wait)
{
    if (capacity == 0 || capacity > 64)
    {
        return -EINVAL;
    }
    struct io_uring_cqe *batch[64];
    unsigned count;
    while (!(count = io_uring_peek_batch_cqe(&p->ring, batch, capacity)) && wait)
    {
        int result = syscall(__NR_io_uring_enter, p->ring.ring_fd, 0, 1, IORING_ENTER_GETEVENTS, NULL, 0);
        if (result < 0 && errno != EINTR)
        {
            return -errno;
        }
    }
    for (unsigned i = 0; i < count; i++)
    {
        ids[i] = batch[i]->user_data;
        results[i] = batch[i]->res;
        if (ids[i] == 0)
        {
            if (results[i] < 0)
            {
                return results[i];
            }
            uint64_t value;
            ssize_t result;
            do
            {
                result = read(p->wake, &value, sizeof(value));
            } while (result < 0 && errno == EINTR);
            if (result < 0 && errno != EAGAIN)
            {
                return -errno;
            }
            p->rearm_wake = 1;
        }
    }
    io_uring_cq_advance(&p->ring, count);
    return (int)count;
}

// Only valid when the pump exclusively owns the SQ; never hold the producer lock.
int np_submit_and_collect(struct np_ring *p, uint64_t *ids, int *results, unsigned capacity, int wait)
{
    if (capacity == 0 || capacity > 64)
    {
        return -EINVAL;
    }
    int status = rearm_wake(p);
    if (status < 0)
    {
        return status;
    }
    do
    {
        unsigned minimum = wait && !io_uring_cq_ready(&p->ring) ? 1 : 0;
        status = io_uring_submit_and_wait(&p->ring, minimum);
    } while (status == -EINTR);
    if (status < 0)
    {
        return status;
    }
    return np_collect(p, ids, results, capacity, 0);
}

int np_wake(struct np_ring *p)
{
    uint64_t value = 1;
    ssize_t result;
    do
    {
        result = write(p->wake, &value, sizeof(value));
    } while (result < 0 && errno == EINTR);
    return result < 0 && errno != EAGAIN ? -errno : 0;
}

int np_enqueue(struct np_ring *p, int operation, int fd, void *buffer, unsigned length, uint64_t id)
{
    struct io_uring_sqe *sqe = get_sqe(p);
    if (!sqe)
    {
        return -errno;
    }
    switch (operation)
    {
        case 0:
            io_uring_prep_accept(sqe, fd, NULL, NULL, SOCK_CLOEXEC);
            break;
        case 1:
            io_uring_prep_recv(sqe, fd, buffer, length, 0);
            break;
        case 2:
            io_uring_prep_send(sqe, fd, buffer, length, MSG_NOSIGNAL);
            break;
        case 4:
            io_uring_prep_multishot_accept(sqe, fd, NULL, NULL, SOCK_CLOEXEC);
            break;
        default:
            abort();
    }
    io_uring_sqe_set_data64(sqe, id);
    return 0;
}

int np_try_send(intptr_t fd, const void *buffer, unsigned length)
{
    ssize_t result;
    do
    {
        result = send((int)fd, buffer, length, MSG_DONTWAIT | MSG_NOSIGNAL);
    } while (result < 0 && errno == EINTR);
    return result < 0 ? -errno : (int)result;
}

int np_try_accept(intptr_t fd)
{
    int result;
    do
    {
        result = accept4((int)fd, NULL, NULL, SOCK_CLOEXEC);
    } while (result < 0 && errno == EINTR);
    return result < 0 ? -errno : result;
}

// Historical standalone probe only; the managed transport has no direct-recv import.
int np_try_recv(intptr_t fd, void *buffer, unsigned length)
{
    ssize_t result;
    do
    {
        result = recv((int)fd, buffer, length, MSG_DONTWAIT);
    } while (result < 0 && errno == EINTR);
    return result < 0 ? -errno : (int)result;
}

int np_cancel(struct np_ring *p, uint64_t id)
{
    struct io_uring_sqe *sqe = get_sqe(p);
    if (!sqe)
    {
        return -errno;
    }
    io_uring_prep_cancel64(sqe, id, 0);
    io_uring_sqe_set_data64(sqe, 1);
    return 0;
}

int np_wait(struct np_ring *p, uint64_t *ids, int *results, unsigned capacity, unsigned *flags)
{
    if (capacity == 0 || capacity > 64)
    {
        return -EINVAL;
    }
    int status = io_uring_submit(&p->ring);
    if (status < 0)
    {
        return status;
    }
    struct io_uring_cqe *cqe;
    do
    {
        status = io_uring_wait_cqe(&p->ring, &cqe);
    } while (status == -EINTR);
    if (status < 0)
    {
        return status;
    }

    struct io_uring_cqe *batch[64];
    unsigned count = io_uring_peek_batch_cqe(&p->ring, batch, capacity);
    int wake = 0;
    for (unsigned i = 0; i < count; i++)
    {
        ids[i] = batch[i]->user_data;
        results[i] = batch[i]->res;
        flags[i] = batch[i]->flags;
        if (ids[i] == 0)
        {
            if (results[i] < 0)
            {
                return results[i];
            }
            wake = 1;
        }
    }
    io_uring_cq_advance(&p->ring, count);
    if (wake)
    {
        uint64_t value;
        ssize_t count;
        do
        {
            count = read(p->wake, &value, sizeof(value));
        } while (count < 0 && errno == EINTR);
        if (count < 0 && errno != EAGAIN)
        {
            return -errno;
        }
        status = arm_wake(p);
        if (status < 0)
        {
            return status;
        }
    }
    return (int)count;
}

void np_destroy(struct np_ring *p)
{
    io_uring_queue_exit(&p->ring);
    close(p->wake);
    free(p);
}
