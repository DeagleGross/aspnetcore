// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.OutputCaching;

/// <summary>
/// A policy which applies the HTTP caching semantics of the Response Caching middleware:
/// request and response <c>Cache-Control</c> directives decide whether a response is cached, for how long, and whether a cached entry can be served.
/// </summary>
/// <remarks>
/// Like Response Caching, the cache key varies by the request headers listed in the response <c>Vary</c> header.
/// Output Caching computes the lookup key before the response is produced, so the header names are remembered in the
/// store for the next requests, and the storage key is recomputed once the response is produced.
/// Header names declared upfront (e.g. with <see cref="OutputCachePolicyBuilder.SetVaryByHeader(string[])"/>) are always added.
/// </remarks>
internal sealed class ResponseCachingPolicy : IOutputCachePolicy
{
    internal static readonly TimeSpan DefaultExpirationTimeSpan = TimeSpan.FromSeconds(10);

    private const char KeyDelimiter = '\x1e';
    private const char KeySubDelimiter = '\x1f';
    private const string VaryRulesKeyPrefix = "ResponseCachingVaryRules";

    public static readonly ResponseCachingPolicy Instance = new();

    private ResponseCachingPolicy()
    {
    }

    /// <inheritdoc />
    async ValueTask IOutputCachePolicy.CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var attemptResponseCaching = AttemptResponseCaching(context);
        context.EnableOutputCaching = true;
        context.AllowCacheLookup = attemptResponseCaching && AllowCacheLookup(context);
        context.AllowCacheStorage = attemptResponseCaching && AllowCacheStorage(context);

        // Like Response Caching, concurrent requests are not coalesced. They could also vary by headers which are only
        // known once the response is produced, and must not be served a response produced for different header values.
        context.AllowLocking = false;

        // The cache key is computed before the response is produced, so query keys can't be selected by the response.
        context.CacheVaryByRules.QueryKeys = "*";

        // Also read when only storage is allowed: storing a response which no longer varies must forget the header names.
        var learnedHeaderNames = context.AllowCacheLookup || context.AllowCacheStorage ? await GetLearnedVaryHeaderNamesAsync(context, cancellationToken) : default;
        context.ResponseCachingLearnedVaryHeaderNames = (context.CacheVaryByRules.HeaderNames.Count, learnedHeaderNames.Count);
        if (learnedHeaderNames.Count > 0)
        {
            context.CacheVaryByRules.HeaderNames = StringValues.Concat(context.CacheVaryByRules.HeaderNames, learnedHeaderNames);
        }
    }

    /// <inheritdoc />
    ValueTask IOutputCachePolicy.ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (!IsCachedEntryFresh(context))
        {
            context.IsCacheEntryFresh = false;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    async ValueTask IOutputCachePolicy.ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (!context.AllowCacheStorage)
        {
            return;
        }

        var responseTime = context.ResponseTime ?? DateTimeOffset.UtcNow;

        if (!IsResponseCacheable(context, responseTime, out var responseSharedMaxAge, out var responseMaxAge, out var responseExpires))
        {
            context.AllowCacheStorage = false;
            return;
        }

        context.ResponseExpirationTimeSpan = responseSharedMaxAge
            ?? responseMaxAge
            ?? (responseExpires - responseTime)
            ?? context.ResponseExpirationTimeSpan
            ?? DefaultExpirationTimeSpan;

        await ApplyResponseVaryHeaderNamesAsync(context, context.ResponseExpirationTimeSpan.Value, cancellationToken);

        if (HasUndeclaredVaryHeaders(context))
        {
            context.AllowCacheStorage = false;
        }
    }

    // Replaces the header names learned from a previous response with the ones of the current response, and remembers them
    // for the next requests. The rest of the header names are unchanged so that lookup and storage keys are computed identically.
    internal static async ValueTask ApplyResponseVaryHeaderNamesAsync(OutputCacheContext context, TimeSpan validFor, CancellationToken cancellationToken)
    {
        var responseHeaderNames = NormalizeHeaderNames(GetVaryHeaders(context.HttpContext.Response));
        var (index, count) = context.ResponseCachingLearnedVaryHeaderNames;
        var headerNames = context.CacheVaryByRules.HeaderNames;

        // Another policy could have replaced the header names after they were learned.
        if (index < 0 || index + count > headerNames.Count)
        {
            (index, count) = (headerNames.Count, 0);
        }

        var allHeaderNames = headerNames.ToArray();
        if (!allHeaderNames.AsSpan(index, count).SequenceEqual(responseHeaderNames.ToArray()))
        {
            string?[] newHeaderNames = [.. allHeaderNames[..index], .. responseHeaderNames, .. allHeaderNames[(index + count)..]];
            context.CacheVaryByRules.HeaderNames = newHeaderNames;
            context.ResponseCachingLearnedVaryHeaderNames = (index, responseHeaderNames.Count);

            // The key used for the lookup doesn't contain the new header names, it's recomputed before the response is stored.
            context.CacheKey = string.Empty;
        }

        if (responseHeaderNames.Count > 0 || count > 0)
        {
            await SetLearnedVaryHeaderNamesAsync(context, responseHeaderNames, validFor, cancellationToken);
        }
    }

    internal static bool AttemptResponseCaching(OutputCacheContext context)
    {
        var request = context.HttpContext.Request;

        // Verify the method
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        // Verify existence of authorization headers
        if (!StringValues.IsNullOrEmpty(request.Headers.Authorization))
        {
            return false;
        }

        return true;
    }

    internal static bool AllowCacheLookup(OutputCacheContext context)
    {
        var requestHeaders = context.HttpContext.Request.Headers;
        var cacheControl = requestHeaders.CacheControl;

        // Verify request cache-control parameters
        if (!StringValues.IsNullOrEmpty(cacheControl))
        {
            if (HeaderUtilities.ContainsCacheDirective(cacheControl, CacheControlHeaderValue.NoCacheString))
            {
                return false;
            }
        }
        else
        {
            // Support for legacy HTTP 1.0 cache directive
            if (HeaderUtilities.ContainsCacheDirective(requestHeaders.Pragma, CacheControlHeaderValue.NoCacheString))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool AllowCacheStorage(OutputCacheContext context)
    {
        // Check request no-store
        return !HeaderUtilities.ContainsCacheDirective(context.HttpContext.Request.Headers.CacheControl, CacheControlHeaderValue.NoStoreString);
    }

    internal static bool IsResponseCacheable(OutputCacheContext context, DateTimeOffset responseTime, out TimeSpan? responseSharedMaxAge, out TimeSpan? responseMaxAge, out DateTimeOffset? responseExpires)
    {
        var response = context.HttpContext.Response;
        var responseCacheControlHeader = response.Headers.CacheControl;

        HeaderUtilities.TryParseSeconds(responseCacheControlHeader, CacheControlHeaderValue.SharedMaxAgeString, out responseSharedMaxAge);
        HeaderUtilities.TryParseSeconds(responseCacheControlHeader, CacheControlHeaderValue.MaxAgeString, out responseMaxAge);
        responseExpires = HeaderUtilities.TryParseDate(response.Headers.Expires.ToString(), out var expires) ? expires : null;

        // Only cache pages explicitly marked with public
        if (!HeaderUtilities.ContainsCacheDirective(responseCacheControlHeader, CacheControlHeaderValue.PublicString))
        {
            return false;
        }

        // Check response no-store
        if (HeaderUtilities.ContainsCacheDirective(responseCacheControlHeader, CacheControlHeaderValue.NoStoreString))
        {
            return false;
        }

        // Check no-cache
        if (HeaderUtilities.ContainsCacheDirective(responseCacheControlHeader, CacheControlHeaderValue.NoCacheString))
        {
            return false;
        }

        // Do not cache responses with Set-Cookie headers
        if (!StringValues.IsNullOrEmpty(response.Headers.SetCookie))
        {
            return false;
        }

        // Do not cache responses varying by *
        foreach (var varyHeader in GetVaryHeaders(response))
        {
            if (varyHeader == "*")
            {
                return false;
            }
        }

        // Check private
        if (HeaderUtilities.ContainsCacheDirective(responseCacheControlHeader, CacheControlHeaderValue.PrivateString))
        {
            return false;
        }

        // Check response code
        if (response.StatusCode != StatusCodes.Status200OK)
        {
            return false;
        }

        // Check response freshness
        DateTimeOffset? responseDate = HeaderUtilities.TryParseDate(response.Headers.Date.ToString(), out var date) ? date : null;
        if (!responseDate.HasValue)
        {
            if (!responseSharedMaxAge.HasValue &&
                !responseMaxAge.HasValue &&
                responseTime >= responseExpires)
            {
                return false;
            }
        }
        else
        {
            var age = responseTime - responseDate.Value;

            // Validate shared max age
            if (age >= responseSharedMaxAge)
            {
                return false;
            }
            else if (!responseSharedMaxAge.HasValue)
            {
                // Validate max age
                if (age >= responseMaxAge)
                {
                    return false;
                }
                else if (!responseMaxAge.HasValue)
                {
                    // Validate expiration
                    if (responseTime >= responseExpires)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    internal static bool IsCachedEntryFresh(OutputCacheContext context)
    {
        var age = context.CachedEntryAge;
        var cachedCacheControlHeaders = context.CachedResponse?.FindHeader(HeaderNames.CacheControl) ?? default;
        var requestCacheControlHeaders = context.HttpContext.Request.Headers.CacheControl;

        // Add min-fresh requirements
        if (HeaderUtilities.TryParseSeconds(requestCacheControlHeaders, CacheControlHeaderValue.MinFreshString, out var minFresh))
        {
            age += minFresh.Value;
        }

        // Validate shared max age, this overrides any max age settings for shared caches
        HeaderUtilities.TryParseSeconds(cachedCacheControlHeaders, CacheControlHeaderValue.SharedMaxAgeString, out var cachedSharedMaxAge);

        if (age >= cachedSharedMaxAge)
        {
            // shared max age implies must revalidate
            return false;
        }
        else if (!cachedSharedMaxAge.HasValue)
        {
            HeaderUtilities.TryParseSeconds(requestCacheControlHeaders, CacheControlHeaderValue.MaxAgeString, out var requestMaxAge);
            HeaderUtilities.TryParseSeconds(cachedCacheControlHeaders, CacheControlHeaderValue.MaxAgeString, out var cachedMaxAge);

            var lowestMaxAge = cachedMaxAge < requestMaxAge ? cachedMaxAge : requestMaxAge ?? cachedMaxAge;
            // Validate max age
            if (age >= lowestMaxAge)
            {
                // Must revalidate or proxy revalidate
                if (HeaderUtilities.ContainsCacheDirective(cachedCacheControlHeaders, CacheControlHeaderValue.MustRevalidateString)
                    || HeaderUtilities.ContainsCacheDirective(cachedCacheControlHeaders, CacheControlHeaderValue.ProxyRevalidateString))
                {
                    return false;
                }

                var maxStaleExist = HeaderUtilities.ContainsCacheDirective(requestCacheControlHeaders, CacheControlHeaderValue.MaxStaleString);
                HeaderUtilities.TryParseSeconds(requestCacheControlHeaders, CacheControlHeaderValue.MaxStaleString, out var requestMaxStale);

                // Request allows stale values with no age limit
                if (maxStaleExist && !requestMaxStale.HasValue)
                {
                    return true;
                }

                // Request allows stale values with age limit
                if (requestMaxStale.HasValue && age - lowestMaxAge < requestMaxStale)
                {
                    return true;
                }

                return false;
            }
            else if (!cachedMaxAge.HasValue && !requestMaxAge.HasValue)
            {
                // Validate expiration
                var cachedExpires = context.CachedResponse?.FindHeader(HeaderNames.Expires) ?? default;
                if (HeaderUtilities.TryParseDate(cachedExpires.ToString(), out var expires) &&
                    context.ResponseTime!.Value >= expires)
                {
                    return false;
                }
            }
        }

        return true;
    }

    // Response Caching adds the request values of the Vary headers to the cache key after the response is produced.
    // Output Caching computes the cache key before, so responses varying by headers which are not part of it can't be stored.
    internal static bool HasUndeclaredVaryHeaders(OutputCacheContext context)
    {
        var declaredHeaderNames = context.CacheVaryByRules.HeaderNames;

        foreach (var varyHeader in GetVaryHeaders(context.HttpContext.Response))
        {
            var isDeclared = false;
            foreach (var declaredHeaderName in declaredHeaderNames)
            {
                if (string.Equals(varyHeader, declaredHeaderName, StringComparison.OrdinalIgnoreCase))
                {
                    isDeclared = true;
                    break;
                }
            }

            if (!isDeclared)
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask<StringValues> GetLearnedVaryHeaderNamesAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var store = context.HttpContext.RequestServices?.GetService<IOutputCacheStore>();
        var key = GetVaryRulesKey(context);
        if (store is null || key is null)
        {
            return default;
        }

        var value = await store.GetAsync(key, cancellationToken);
        if (value is null || value.Length == 0)
        {
            return default;
        }

        return new StringValues(Encoding.UTF8.GetString(value).Split(','));
    }

    private static async ValueTask SetLearnedVaryHeaderNamesAsync(OutputCacheContext context, StringValues headerNames, TimeSpan validFor, CancellationToken cancellationToken)
    {
        var store = context.HttpContext.RequestServices?.GetService<IOutputCacheStore>();
        var key = GetVaryRulesKey(context);
        if (store is null || key is null || validFor <= TimeSpan.Zero)
        {
            return;
        }

        var value = headerNames.Count == 0 ? [] : Encoding.UTF8.GetBytes(string.Join(',', headerNames.ToArray()));
        await store.SetAsync(key, value, tags: null, validFor, cancellationToken);
    }

    // Like the Response Caching base key, the Vary header names are remembered per method, scheme, host and path.
    private static string? GetVaryRulesKey(OutputCacheContext context)
    {
        var request = context.HttpContext.Request;
        var pathBase = request.PathBase.Value ?? string.Empty;
        var path = request.Path.Value ?? string.Empty;

        // Like the Output Caching keys, paths containing the key delimiters are not cached.
        if (pathBase.AsSpan().ContainsAny(KeyDelimiter, KeySubDelimiter) || path.AsSpan().ContainsAny(KeyDelimiter, KeySubDelimiter))
        {
            return null;
        }

        var useCaseSensitivePaths = context.HttpContext.RequestServices?.GetService<IOptions<OutputCacheOptions>>()?.Value.UseCaseSensitivePaths ?? false;
        if (!useCaseSensitivePaths)
        {
            pathBase = pathBase.ToUpperInvariant();
            path = path.ToUpperInvariant();
        }

        return string.Join(KeyDelimiter, VaryRulesKeyPrefix, request.Method.ToUpperInvariant(), request.Scheme.ToUpperInvariant(), request.Host.Value?.ToUpperInvariant(), pathBase, path);
    }

    // Like Response Caching, the order and casing of the header names don't matter.
    private static StringValues NormalizeHeaderNames(IEnumerable<string> headerNames)
    {
        var normalized = headerNames
            .Select(headerName => headerName.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new StringValues(normalized);
    }

    private static IEnumerable<string> GetVaryHeaders(HttpResponse response)
    {
        foreach (var value in response.Headers.Vary)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            foreach (var segment in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return segment;
            }
        }
    }
}
