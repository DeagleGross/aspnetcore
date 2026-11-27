// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.OutputCaching;

/// <summary>
/// Stores responses the way the Response Caching middleware does, so the response <c>Vary</c> header extends the cache key.
/// </summary>
/// <remarks>
/// Two entries are stored for each response:
/// <list type="bullet">
/// <item>The vary rules, under <see cref="OutputCacheContext.CacheKey"/> and a suffix: a random prefix and the header names listed by the response <c>Vary</c> header.</item>
/// <item>The response, under <see cref="OutputCacheContext.CacheKey"/>, the prefix, and the request values of these headers.</item>
/// </list>
/// The vary rules are written again with each response, so they follow the latest response. When they change, a new prefix is used,
/// so the responses stored with the previous rules can't be found anymore.
/// </remarks>
internal sealed class ResponseCachingOutputCacheEntryStrategy : IOutputCacheEntryStrategy
{
    private const char KeyDelimiter = OutputCacheKeyProvider.KeyDelimiter;
    private const char KeySubDelimiter = OutputCacheKeyProvider.KeySubDelimiter;
    private const char KeyNameValueDelimiter = OutputCacheKeyProvider.KeyNameValueDelimiter;

    // Default cache keys only contain single letter segments after the delimiter, so this suffix can't match one.
    private const string VaryByRulesKeySuffix = "VaryByRules";

    private readonly IOutputCacheStore _store;
    private readonly ILogger _logger;

    public ResponseCachingOutputCacheEntryStrategy(IOutputCacheStore store, ILogger logger)
    {
        _store = store;
        _logger = logger;
    }

    public async ValueTask<OutputCacheEntry?> GetAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var varyByRules = await GetVaryByRulesAsync(context.CacheKey, cancellationToken);
        if (varyByRules is null || !TryCreateResponseKey(context, varyByRules, out var responseKey))
        {
            return null;
        }

        return await OutputCacheEntryFormatter.GetAsync(responseKey, _store, cancellationToken);
    }

    public async ValueTask StoreAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var varyHeaderNames = GetNormalizedVaryHeaderNames(context.HttpContext.Response.Headers);
        foreach (var varyHeaderName in varyHeaderNames)
        {
            if (OutputCacheKeyProvider.ContainsDelimiters(varyHeaderName))
            {
                return;
            }
        }

        VaryByRules? varyByRules;
        try
        {
            varyByRules = await GetVaryByRulesAsync(context.CacheKey, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.UnableToQueryOutputCache(ex);
            varyByRules = null;
        }

        // Keep the prefix while the rules are the same, so the responses stored for other header values remain valid.
        if (varyByRules is null || !varyByRules.HeaderNames.AsSpan().SequenceEqual(varyHeaderNames))
        {
            varyByRules = new VaryByRules(Guid.NewGuid().ToString("N"), varyHeaderNames);
        }

        if (!TryCreateResponseKey(context, varyByRules, out var responseKey))
        {
            return;
        }

        try
        {
            // Always written, to extend the expiration of the rules to the one of the latest response.
            await _store.SetAsync(GetVaryByRulesKey(context.CacheKey), varyByRules.Serialize(), tags: null, context.CachedResponseValidFor, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.UnableToWriteToOutputCache(ex);
            return;
        }

        await OutputCacheEntryFormatter.StoreAsync(responseKey, context.CachedResponse!, context.Tags, context.CachedResponseValidFor, _store, _logger, cancellationToken);
    }

    private async ValueTask<VaryByRules?> GetVaryByRulesAsync(string cacheKey, CancellationToken cancellationToken)
    {
        var content = await _store.GetAsync(GetVaryByRulesKey(cacheKey), cancellationToken);
        return content is null ? null : VaryByRules.Deserialize(content);
    }

    private static string GetVaryByRulesKey(string cacheKey) => string.Concat(cacheKey, KeyDelimiter.ToString(), VaryByRulesKeySuffix);

    // <CacheKey><delimiter><Prefix><delimiter>H<delimiter>HeaderName<key-value-delimiter>HeaderValue1<subdelimiter>HeaderValue2
    private static bool TryCreateResponseKey(OutputCacheContext context, VaryByRules varyByRules, out string responseKey)
    {
        var builder = new StringBuilder(context.CacheKey)
            .Append(KeyDelimiter)
            .Append(varyByRules.Prefix);

        if (varyByRules.HeaderNames.Length > 0)
        {
            builder.Append(KeyDelimiter).Append('H');

            var requestHeaders = context.HttpContext.Request.Headers;
            foreach (var headerName in varyByRules.HeaderNames)
            {
                builder.Append(KeyDelimiter).Append(headerName).Append(KeyNameValueDelimiter);

                var headerValues = requestHeaders[headerName];
                for (var i = 0; i < headerValues.Count; i++)
                {
                    var headerValue = headerValues[i];
                    if (OutputCacheKeyProvider.ContainsDelimiters(headerValue))
                    {
                        responseKey = string.Empty;
                        return false;
                    }

                    if (i > 0)
                    {
                        builder.Append(KeySubDelimiter);
                    }

                    builder.Append(headerValue);
                }
            }
        }

        responseKey = builder.ToString();
        return true;
    }

    // Same normalization as Response Caching: upper case and ordinal order, so the order and casing of the Vary header don't create new rules.
    private static string[] GetNormalizedVaryHeaderNames(IHeaderDictionary responseHeaders)
    {
        var varyHeaderNames = responseHeaders.GetCommaSeparatedValues(HeaderNames.Vary);
        for (var i = 0; i < varyHeaderNames.Length; i++)
        {
            varyHeaderNames[i] = varyHeaderNames[i].ToUpperInvariant();
        }

        Array.Sort(varyHeaderNames, StringComparer.Ordinal);
        return varyHeaderNames;
    }

    private sealed class VaryByRules(string prefix, string[] headerNames)
    {
        public string Prefix { get; } = prefix;

        public string[] HeaderNames { get; } = headerNames;

        // Format: Prefix<subdelimiter>HeaderName1<subdelimiter>HeaderName2
        public byte[] Serialize()
        {
            var builder = new StringBuilder(Prefix);
            foreach (var headerName in HeaderNames)
            {
                builder.Append(KeySubDelimiter).Append(headerName);
            }

            return Encoding.UTF8.GetBytes(builder.ToString());
        }

        public static VaryByRules Deserialize(byte[] content)
        {
            var segments = Encoding.UTF8.GetString(content).Split(KeySubDelimiter);
            return new VaryByRules(segments[0], segments[1..]);
        }
    }
}
