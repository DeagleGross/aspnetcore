# API Proposal: Redis GCRA Rate Limiter

Related: https://github.com/dotnet/aspnetcore/issues/65792, https://github.com/dotnet/aspnetcore/issues/41861, https://github.com/dotnet/aspnetcore/issues/53426

## Motivation

ASP.NET Core has a rate limiting middleware (shipped in .NET 7) with four in-process algorithms: fixed window, sliding window, token bucket, and concurrency limiter. All of them store state in-memory, meaning each app server tracks limits independently. With N servers, a client can achieve N× the intended rate by distributing requests across servers.

There is currently **no distributed rate limiting support** in ASP.NET Core. Issues [#41861](https://github.com/dotnet/aspnetcore/issues/41861) and [#53426](https://github.com/dotnet/aspnetcore/issues/53426) have been open since 2022/2024 requesting this.

Redis is adding a native [GCRA (Generic Cell Rate Algorithm) command](https://github.com/redis/redis/pull/14826). GCRA is uniquely suited for distributed rate limiting because:

1. **Single atomic command** — No Lua scripts or multi-key transactions needed
2. **Minimal state** — Stores exactly one timestamp per key (the Theoretical Arrival Time)
3. **Native burst control** — `max_burst` parameter controls burst allowance
4. **Rich response** — Returns `remaining`, `retry_after`, and `reset_after` in one call

StackExchange.Redis is [adding client support](https://github.com/StackExchange/StackExchange.Redis/pull/3034) via `StringGcraRateLimitAsync()`.

## Current State of Code

### Rate limiting abstractions (dotnet/runtime — `System.Threading.RateLimiting`)

The runtime provides the abstract base classes and in-process implementations:

```csharp
// Base abstraction — two acquire paths:
abstract class RateLimiter
{
    RateLimitLease AttemptAcquire(int permitCount);           // sync, fast path
    ValueTask<RateLimitLease> AcquireAsync(int permitCount);  // async, can queue
}

// Partition factory methods for in-process algorithms:
RateLimitPartition.GetTokenBucketLimiter<TKey>(partitionKey, factory)
RateLimitPartition.GetSlidingWindowLimiter<TKey>(partitionKey, factory)
RateLimitPartition.GetFixedWindowLimiter<TKey>(partitionKey, factory)
RateLimitPartition.GetConcurrencyLimiter<TKey>(partitionKey, factory)
RateLimitPartition.Get<TKey>(partitionKey, factory)            // generic
```

### Rate limiting middleware (dotnet/aspnetcore — `Microsoft.AspNetCore.RateLimiting`)

The aspnetcore repo provides middleware integration and convenience APIs:

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.AddTokenBucketLimiter("myPolicy", opts => { ... });
    options.AddSlidingWindowLimiter("otherPolicy", opts => { ... });
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(...);
});
app.UseRateLimiter();
app.MapGet("/api", handler).RequireRateLimiting("myPolicy");
```

### Existing Redis packages in aspnetcore

ASP.NET Core already ships four packages that depend on `StackExchangeRedis`:

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.Caching.StackExchangeRedis` | `IDistributedCache` implementation |
| `Microsoft.AspNetCore.SignalR.StackExchangeRedis` | SignalR scale-out backplane |
| `Microsoft.AspNetCore.DataProtection.StackExchangeRedis` | Shared encryption key storage |
| `Microsoft.AspNetCore.OutputCaching.StackExchangeRedis` | Shared output cache store |

All follow the same pattern: a separate NuGet package in the aspnetcore repo that depends on `StackExchange.Redis` and implements an ASP.NET Core abstraction backed by Redis.

### What's missing

There is no distributed `RateLimiter` implementation. The gap:

```
Caching:    IDistributedCache (abstraction) → MemoryDistributedCache | RedisCache
RateLimiting: RateLimiter (abstraction)     → TokenBucket | SlidingWindow | ??? Redis
```

## Design Principles

- **Plugs into existing middleware** — Same `AddRateLimiter()`, `UseRateLimiter()`, `RequireRateLimiting()` pipeline. Users swap an in-process limiter for a Redis-backed one by changing the registration, not the middleware.
- **Follows established Redis package pattern** — Separate NuGet package, same connection options shape as the other four Redis packages (`Configuration`, `ConfigurationOptions`, `ConnectionMultiplexerFactory`, `InstanceName`).
- **No runtime repo changes required** — The generic `RateLimitPartition.Get<TKey>()` factory already exists. No new abstractions needed in `System.Threading.RateLimiting`.
- **Resilient by default** — Configurable behavior when Redis is unavailable (fail-open or fail-closed).

## Architecture

### Package: `Microsoft.AspNetCore.RateLimiting.StackExchangeRedis`

A new package in `src/Middleware/RateLimiting.StackExchangeRedis/` following the same structure as `Microsoft.AspNetCore.OutputCaching.StackExchangeRedis`.

```
src/Middleware/RateLimiting.StackExchangeRedis/
├── src/
│   ├── Microsoft.AspNetCore.RateLimiting.StackExchangeRedis.csproj
│   ├── RedisGcraRateLimiter.cs              // RateLimiter subclass
│   ├── RedisGcraRateLimiterOptions.cs       // GCRA + Redis connection options
│   ├── RedisGcraRateLimitLease.cs           // Lease with retry-after metadata
│   ├── RedisFailurePolicy.cs                // Enum: FailOpen / FailClosed
│   └── RedisGcraRateLimiterExtensions.cs    // AddRedisGcraLimiter() extensions
└── test/
    └── ...
```

Dependencies:
- `StackExchange.Redis` (NuGet)
- `Microsoft.AspNetCore.RateLimiting` (project reference)

### How it fits the existing pipeline

```
                    ┌────────────────────────────────────┐
                    │ dotnet/runtime                      │
                    │ System.Threading.RateLimiting       │
                    │   RateLimiter (abstract)            │
                    │   PartitionedRateLimiter<T>         │
                    │   RateLimitPartition.Get<TKey>()    │
                    │   TokenBucketRateLimiter (in-proc)  │
                    │   SlidingWindowRateLimiter (in-proc)│
                    └───────────────┬────────────────────┘
                                    │ references
                    ┌───────────────┴────────────────────┐
                    │ dotnet/aspnetcore                   │
                    │ Microsoft.AspNetCore.RateLimiting   │
                    │   AddRateLimiter()                  │
                    │   UseRateLimiter()                  │
                    │   RateLimitingMiddleware            │
                    │   AddTokenBucketLimiter() etc.      │
                    └───────────────┬────────────────────┘
                                    │ references
  ┌─────────────────────────────────┴──────────────────────────────┐
  │ dotnet/aspnetcore (NEW)                                        │
  │ Microsoft.AspNetCore.RateLimiting.StackExchangeRedis           │
  │   RedisGcraRateLimiter : RateLimiter                           │
  │   AddRedisGcraLimiter() extension                              │
  │   + StackExchange.Redis dependency                             │
  └────────────────────────────────────────────────────────────────┘
```

### Middleware flow with Redis GCRA

The existing `RateLimitingMiddleware` has a two-phase acquire:

```
TryAcquireAsync():
  1. CombinedAcquire()     — calls AttemptAcquire() [sync]
  2. CombinedWaitAsync()   — calls AcquireAsync()   [async]
```

For Redis GCRA:
- `AttemptAcquire()` → returns a "not acquired" lease (cannot do sync Redis calls). No permits consumed.
- The middleware naturally falls back to `AcquireAsync()` → executes the Redis GCRA command.
- The async overhead is negligible compared to the Redis network round-trip.

## API

### New types in `Microsoft.AspNetCore.RateLimiting.StackExchangeRedis`

```csharp
namespace Microsoft.AspNetCore.RateLimiting.StackExchangeRedis;

/// <summary>
/// Options for a Redis-backed GCRA rate limiter.
/// </summary>
public sealed class RedisGcraRateLimiterOptions
{
    // === GCRA parameters ===

    /// <summary>
    /// Maximum number of requests allowed per <see cref="Period"/>.
    /// </summary>
    public int RequestsPerPeriod { get; set; }

    /// <summary>
    /// The time window for rate limiting.
    /// </summary>
    public TimeSpan Period { get; set; }

    /// <summary>
    /// Additional requests allowed as burst beyond the sustained rate.
    /// Total instantaneous capacity is MaxBurst + 1.
    /// </summary>
    public int MaxBurst { get; set; }

    // === Redis connection (same shape as RedisCacheOptions, RedisOutputCacheOptions) ===

    /// <summary>
    /// The Redis configuration string.
    /// </summary>
    public string? Configuration { get; set; }

    /// <summary>
    /// The Redis configuration options. Preferred over <see cref="Configuration"/>.
    /// </summary>
    public ConfigurationOptions? ConfigurationOptions { get; set; }

    /// <summary>
    /// Factory to create the ConnectionMultiplexer instance.
    /// Preferred over <see cref="Configuration"/> and <see cref="ConfigurationOptions"/>.
    /// </summary>
    public Func<Task<IConnectionMultiplexer>>? ConnectionMultiplexerFactory { get; set; }

    /// <summary>
    /// Prefix for Redis keys. Allows partitioning a single Redis instance
    /// for multiple apps. If set, rate limiter keys are prefixed with this value.
    /// </summary>
    public string? InstanceName { get; set; }

    // === Resilience ===

    /// <summary>
    /// Behavior when Redis is unavailable. Default: <see cref="RedisFailurePolicy.FailOpen"/>.
    /// </summary>
    public RedisFailurePolicy FailurePolicy { get; set; } = RedisFailurePolicy.FailOpen;
}

/// <summary>
/// Determines behavior when Redis is unavailable.
/// </summary>
public enum RedisFailurePolicy
{
    /// <summary>Allow all requests when Redis is unavailable.</summary>
    FailOpen,
    /// <summary>Reject all requests when Redis is unavailable.</summary>
    FailClosed
}
```

```csharp
namespace Microsoft.AspNetCore.RateLimiting.StackExchangeRedis;

/// <summary>
/// A rate limiter backed by the Redis GCRA command.
/// Provides distributed, centralized rate limiting across multiple app instances.
/// </summary>
public sealed class RedisGcraRateLimiter : RateLimiter
{
    /// <summary>
    /// Creates a new <see cref="RedisGcraRateLimiter"/>.
    /// </summary>
    public RedisGcraRateLimiter(string redisKey, RedisGcraRateLimiterOptions options);

    /// <inheritdoc/>
    /// <remarks>
    /// Always returns a lease with <see cref="RateLimitLease.IsAcquired"/> set to false,
    /// because Redis operations are inherently async. The middleware will fall back
    /// to <see cref="RateLimiter.AcquireAsync"/>.
    /// </remarks>
    protected override RateLimitLease AttemptAcquireCore(int permitCount);

    /// <inheritdoc/>
    /// <remarks>
    /// Executes the Redis GCRA command. The <paramref name="permitCount"/> is passed
    /// as the TOKENS parameter, supporting variable-cost requests.
    /// On success, the returned lease includes <see cref="MetadataName.RetryAfter"/> metadata.
    /// </remarks>
    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount, CancellationToken cancellationToken);

    /// <inheritdoc/>
    public override RateLimiterStatistics? GetStatistics();

    /// <inheritdoc/>
    public override TimeSpan? IdleDuration { get; }
}
```

### Extension methods

```csharp
namespace Microsoft.AspNetCore.RateLimiting;

public static class RedisGcraRateLimiterExtensions
{
    /// <summary>
    /// Registers a Redis GCRA rate limiter with the given policy name.
    /// All requests matching this policy share a single Redis key.
    /// </summary>
    public static RateLimiterOptions AddRedisGcraLimiter(
        this RateLimiterOptions options,
        string policyName,
        Action<RedisGcraRateLimiterOptions> configureOptions);

    /// <summary>
    /// Registers a Redis GCRA rate limiter with per-partition keys.
    /// Each distinct partition key maps to a separate Redis key.
    /// </summary>
    public static RateLimiterOptions AddRedisGcraLimiter<TPartitionKey>(
        this RateLimiterOptions options,
        string policyName,
        Func<HttpContext, TPartitionKey> partitionKeySelector,
        Action<RedisGcraRateLimiterOptions> configureOptions)
        where TPartitionKey : notnull;
}
```

### Lease metadata

The Redis GCRA response includes `retry_after_s`, `remaining`, and `reset_after_s`. Rejected leases expose `MetadataName.RetryAfter` so the `OnRejected` callback can set the `Retry-After` response header:

```csharp
options.OnRejected = async (context, ct) =>
{
    context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
    {
        context.HttpContext.Response.Headers.RetryAfter =
            ((int)retryAfter.TotalSeconds).ToString();
    }
};
```

## Usage Examples

### Global rate limit (all requests share one Redis key)

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.AddRedisGcraLimiter("globalPolicy", opts =>
    {
        opts.Configuration = "localhost:6379";
        opts.RequestsPerPeriod = 1000;
        opts.Period = TimeSpan.FromMinutes(1);
        opts.MaxBurst = 50;
        opts.InstanceName = "myapp:ratelimit:";
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

app.UseRateLimiter();
app.MapGet("/api/data", handler).RequireRateLimiting("globalPolicy");
```

### Per-user rate limit

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.AddRedisGcraLimiter<string>("perUserPolicy",
        httpContext => httpContext.User.Identity?.Name ?? "anonymous",
        opts =>
        {
            opts.Configuration = "localhost:6379";
            opts.RequestsPerPeriod = 100;
            opts.Period = TimeSpan.FromMinutes(1);
            opts.MaxBurst = 10;
        });
});
```

### Mixed: in-process + Redis

```csharp
builder.Services.AddRateLimiter(options =>
{
    // In-process concurrency limit (existing)
    options.AddConcurrencyLimiter("concurrencyPolicy", opts =>
    {
        opts.PermitLimit = 10;
    });

    // Distributed rate limit (new)
    options.AddRedisGcraLimiter<string>("distributedPolicy",
        httpContext => httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        opts =>
        {
            opts.Configuration = "localhost:6379";
            opts.RequestsPerPeriod = 60;
            opts.Period = TimeSpan.FromMinutes(1);
            opts.MaxBurst = 5;
        });
});

app.MapGet("/api/fast", handler).RequireRateLimiting("concurrencyPolicy");
app.MapGet("/api/slow", handler).RequireRateLimiting("distributedPolicy");
```

### Advanced: direct PartitionedRateLimiter usage

For users who want full control (e.g., as a `GlobalLimiter`), the `RedisGcraRateLimiter` can be used with the generic `RateLimitPartition.Get<TKey>()` factory:

```csharp
options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    RateLimitPartition.Get(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: key => new RedisGcraRateLimiter(
            redisKey: key,
            options: new RedisGcraRateLimiterOptions
            {
                ConnectionMultiplexerFactory = () => getSharedMultiplexer(),
                RequestsPerPeriod = 100,
                Period = TimeSpan.FromMinutes(1),
                MaxBurst = 10,
                InstanceName = "myapp:global:",
            })));
```

## Behavioral Details

### AttemptAcquire (sync fast path)

`RedisGcraRateLimiter.AttemptAcquire()` returns a lease with `IsAcquired = false` without consuming any permits. The middleware's `TryAcquireAsync` method then falls back to `AcquireAsync`, which performs the Redis call. This follows the existing contract — `AttemptAcquire` is documented as "fast synchronous attempt" and returning a failed lease is valid behavior.

### Variable cost

The `permitCount` parameter from `AcquireAsync(int permitCount)` is passed as the `TOKENS` parameter to the Redis GCRA command. This supports variable-cost scenarios (e.g., a bulk upload costs 10 tokens, a read costs 1) without additional API.
