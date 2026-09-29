// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#include <openssl/ssl.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

// One borrowed input page and one owned output buffer. C# queues pages and owns all socket I/O.
struct bio_state {
    const unsigned char *input;
    int input_length, input_offset, eof;
    unsigned char output[32768];
    int output_length, output_offset;
    uint64_t input_copied, output_copied;
};
struct bio_status {
    void *output;
    int input_remaining, output_remaining;
    uint64_t input_copied, output_copied;
};
static int create(BIO *bio) { BIO_set_init(bio, 1); return 1; }
static int read_data(BIO *bio, char *destination, size_t capacity, size_t *count)
{
    struct bio_state *s = BIO_get_data(bio);
    BIO_clear_retry_flags(bio);
    size_t available = (size_t)(s->input_length - s->input_offset);
    *count = available < capacity ? available : capacity;
    if (*count) {
        memcpy(destination, s->input + s->input_offset, *count);
        s->input_offset += (int)*count; s->input_copied += *count;
        return 1;
    }
    if (!s->eof) BIO_set_retry_read(bio);
    return 0;
}
static int write_data(BIO *bio, const char *source, size_t length, size_t *count)
{
    struct bio_state *s = BIO_get_data(bio);
    BIO_clear_retry_flags(bio); *count = 0;
    if (s->output_length) { BIO_set_retry_write(bio); return 0; }
    *count = length < sizeof(s->output) ? length : sizeof(s->output);
    memcpy(s->output, source, *count);
    s->output_length = (int)*count; s->output_copied += *count;
    return 1;
}
static long control(BIO *bio, int command, long number, void *pointer)
{
    (void)number; (void)pointer;
    struct bio_state *s = BIO_get_data(bio);
    switch (command) {
    case BIO_CTRL_FLUSH: return 1;
    case BIO_CTRL_PENDING: return s->input_length - s->input_offset;
    case BIO_CTRL_WPENDING: return s->output_length - s->output_offset;
    case BIO_CTRL_EOF: return s->eof && s->input_length == s->input_offset;
    default: return 0;
    }
}
BIO_METHOD *ub_method(void)
{
    BIO_METHOD *method = BIO_meth_new(BIO_TYPE_SOURCE_SINK, "managed io_uring buffers");
    if (method && (!BIO_meth_set_create(method, create) || !BIO_meth_set_read_ex(method, read_data)
        || !BIO_meth_set_write_ex(method, write_data) || !BIO_meth_set_ctrl(method, control))) {
        BIO_meth_free(method); return NULL;
    }
    return method;
}
SSL *ub_session(SSL_CTX *ctx, BIO_METHOD *method, struct bio_state **state)
{
    SSL *ssl = SSL_new(ctx);
    BIO *bio = BIO_new(method);
    *state = calloc(1, sizeof(**state));
    if (!ssl || !bio || !*state) {
        SSL_free(ssl); BIO_free(bio); free(*state); *state = NULL; return NULL;
    }
    BIO_set_data(bio, *state);
    SSL_set_bio(ssl, bio, bio); SSL_set_accept_state(ssl);
    return ssl;
}
void ub_feed(struct bio_state *s, const void *data, int length, int eof)
{
    s->input = data; s->input_length = length; s->input_offset = 0; s->eof = eof;
}
void ub_status(struct bio_state *s, struct bio_status *result)
{
    *result = (struct bio_status){ s->output + s->output_offset, s->input_length - s->input_offset,
        s->output_length - s->output_offset, s->input_copied, s->output_copied };
}
void ub_advance(struct bio_state *s, int count)
{
    s->output_offset += count;
    if (s->output_offset == s->output_length) s->output_length = s->output_offset = 0;
}
void ub_free(struct bio_state *s) { free(s); }
void ub_free_method(BIO_METHOD *method) { BIO_meth_free(method); }
