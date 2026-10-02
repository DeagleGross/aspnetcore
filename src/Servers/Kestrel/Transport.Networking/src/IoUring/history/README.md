# io_uring transport development history

This is the chronological story of the experiments, including improvements, unsuccessful approaches, correctness fixes, and the measurements behind them. It covers the earliest preserved Kestrel measurements on September 19 through October 1, 2026. It complements, rather than replaces, the detailed [transport results](../RPS-RESULTS.md) and [original sample investigation](../../../../samples/NetworkProtoSample/RESULTS.md).

Dates follow the recorded measurement days, not the later commits that collected the work. The early sample's `environment.txt` timestamps place its first iterations on September 19 and 20, using the local UTC+02 calendar. The standalone C report explicitly dates its experiments September 25. No additional dated measurement series was identified for September 21-24; this history does not invent entries for those days.

## Reading order and major results

Approximate changes below are against the stated earlier implementation within that experiment. They are not additive, and they do not all compare against stock.

| Day | Main work | Approximate result |
|---|---|---|
| [September 19](2026-09-19.md) | Initial single-ring Kestrel transport; completion/wake batching; immediate-I/O experiments; graceful teardown; reusable completions | Initial TLS lost to stock. Direct small sends improved plaintext keep-alive about 52% over the ring-only control; immediate accept improved short connections about 94%; graceful shutdown added about 52% over cancellation-first close. Reuse removed about 384 allocated bytes per keep-alive request. |
| [September 20](2026-09-20.md) | Return to full-ring data I/O; submission ownership; eager/multishot accept; deferred task work | Caller-thread SQ preparation regressed badly and was not retained as the default. Eager one-shot accept improved short RPS about 93% over demand-driven accept; multishot was not uniformly better. Deferred task work added roughly 4-8% in the principal median comparisons. |
| [September 25](2026-09-25.md) | Standalone C one-shot/multishot and TLS integration experiments | No repeatable multishot receive win for one-response connections. Read-ahead plus avoiding an unnecessary speculative TLS read improved persistent TLS about 12% over the original fd-bound implementation. |
| [September 26](2026-09-26.md) | Four-pump owned-buffer Kestrel transports; native fd TLS; wake coalescing | New architectures exceeded stock in several keep-alive/TLS cells, but TCP short still lost. Coalescing improved TLS keep-alive about 7-10% in selected comparisons, with regressions elsewhere; it was not a universal RPS win. |
| [September 27](2026-09-27.md) | Transport-driven custom BIO; actual kTLS activation | Custom BIO's median TLS keep-alive was about 12% above fd TLS in one matched batch. Verified software kTLS was about 11% slower than equivalent userspace fd TLS on keep-alive. |
| [September 28](2026-09-28.md) | Guard redundant TLS reads; repair rejected-accept disposal; investigate TCP churn; final-send/shutdown batching | Read guard added about 6-7% in one matched batch. Raw TCP final-send batching took roughly 40k-46k to 84k-85k RPS; BIO TCP improved about 55%. TLS short changed only about +2% for fd TLS and 0% for BIO. |
| [September 29](2026-09-29.md) | Move newer io_uring TCP, fd TLS and BIO state into C#; add raw TCP epoll workers | About 76% less C for io_uring; matched persistent means 1-4% lower. A separate raw-epoll batch measured 59k short / 161k long versus fresh stock 45k / 147k. |
| [October 1](2026-10-01.md) | Epoll worker directly drains output; remove send/completion handoffs; sweep cores, workers and concurrency | Four-core TCP improved about 9% short / 13% long over previous epoll. Eight-core epoll reached about 147k TCP short / 483k TCP long and 15.8k TLS short / 367k TLS long; TLS long +22% versus matched stock, TCP long -4%. |

## Three different implementations, not one accumulating speedup

The original `io_uring` backend is the single-ring managed/native Kestrel prototype developed on September 19-20. Its immediate syscall options, reusable operations, queue alternatives, and accept experiments belong to that implementation.

The standalone C servers from September 25 live in the sibling Network-Transport-Experiments repository. They informed later architecture choices, but use a smaller HTTP implementation and different TLS/workload settings. Their RPS is not directly comparable with Kestrel's.

The newer `IoUringTcp`, `IoUringTls`, and `IoUringBio` backends initially used four native pumps and a shared owned-page managed adapter. They were added from September 26 onward while preserving the original backend, then moved their pumps/connection state into C# on September 29. The adapter's earlier roughly 4 KB/request persistent allocation did not automatically inherit the older implementation's approximately 1.6 KB/request result.

## Afternoon snapshot: September 28, before the epoll experiment

For ongoing comparisons, the chosen native configuration is coalesced wakes plus final-send batching; the fd path also uses the page-return read guard. These remain explicit experiment switches, not a change to their source-code defaults. Historical measurements below are not silently relabeled as measurements with all newer switches enabled.

Every percentage in this convenience table is arithmetic against the stock value in the same column. This combines measurement batches and is not a controlled estimate of each transport's advantage.

| Transport | TCP short RPS | TCP long RPS | TLS short RPS | TLS long RPS |
|---|---:|---:|---:|---:|
| Stock Sockets / SslStream | 51,082 | 180,554 | 4,330 | 114,123 |
| IoUringTcp / SslStream | 84,747 (+65.9%) | 219,731 (+21.7%) | 4,125 (-4.7%) | 121,123 (+6.1%) |
| Fd-bound IoUringTls | N/A | N/A | 7,526 (+73.8%) | 146,725 (+28.6%) |
| Layered IoUringBio | 57,079 (+11.7%) | 213,982 (+18.5%) | 7,723 (+78.3%) | 143,618 (+25.8%) |
| Fd IoUringTls, verified kTLS RX+TX | N/A | N/A | 6,734 (+55.5%) | 130,487 (+14.3%) |
| Custom-BIO IoUringBio, required kTLS | N/A | N/A | Not supported | Not supported |

Stock, fd userspace TLS, and BIO are from the final matched batching experiment. IoUringTcp's TCP short value is the mean of the earlier 84,008 / 85,485 batching-enabled runs; TCP long is a single 219,731-RPS screen. Its SslStream/TLS cells and the kTLS cells are carried from the earlier coalesced matrix, before final-send batching was benchmarked on those configurations. kTLS later passed the batching correctness checks but was not rebenchmarked. The [September 28 entry](2026-09-28.md) preserves the actual off/on comparisons.

The later evening [native epoll TLS experiment](../../Epoll/README.md) added a separate readiness backend and fresh controls. Its final four-worker mean was 5,744 TLS short / 127,039 TLS long RPS versus stock 3,160 / 95,058. That batch is recorded separately rather than retroactively replacing the afternoon baselines.

## How to interpret and extend this history

Early Kestrel runs used monotonic-clock wrk 4.1.0, typically two client cores, 16 short or 64 persistent connections, and separate warmup/measurement intervals. The later matrices used rate-controlled wrk2, twelve client cores, 1,200 connections, and usually 15-second measurements. The standalone C experiments had their own setup. Do not join these series into a single percentage-growth curve.

All measurements are from a shared WSL/Windows loopback machine, not a dedicated performance lab. CPU affinity separates the selected client/server cores but does not eliminate host activity, cache sharing, frequency changes, or drift. Later offered loads intentionally exceeded capacity, so achieved RPS is not sustainable latency-SLO capacity.

Each daily entry records what changed, why, the relevant baseline, and evidence locations. Keep failed experiments visible; distinguish a correctness repair from a throughput optimization. Add a new dated entry when work continues, and update this index without rewriting old results to match the newest implementation.

Raw result directories are gitignored and may not exist in a fresh clone. The Markdown reports preserve the important numbers and limitations; raw-directory references allow deeper inspection on the measurement machine. None of this is an approved or shipping framework transport.
