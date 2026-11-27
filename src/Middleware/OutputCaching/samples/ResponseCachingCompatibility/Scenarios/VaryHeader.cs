// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace ResponseCachingCompatibility.Scenarios;

/// <summary>
/// The endpoint returns content depending on the Accept-Language request header, and says so with the
/// "Vary: Accept-Language" response header. Response Caching then stores one response per language.
/// </summary>
/// <remarks>
/// Known difference: Output Caching computes the cache key before the endpoint runs, so it can't learn the vary rules from
/// the response. With UseResponseCachingSemantics() such responses aren't stored, while the default policy serves the
/// first language to everyone. Declaring the header upfront with SetVaryByHeader restores the Response Caching behavior.
/// </remarks>
public sealed class VaryHeader : Scenario
{
    public override string Description => "Endpoint sets 'Vary: Accept-Language' (known difference: declare it with SetVaryByHeader)";

    // Before: Response Caching.
    public override WebApplication CreateResponseCachingApp(WebApplicationBuilder builder)
    {
        builder.Services.AddResponseCaching();

        var app = builder.Build();
        app.UseResponseCaching();
        MapEndpoints(app);
        return app;
    }

    // After: Output Caching with the Response Caching semantics. Only the two caching lines change.
    public override WebApplication CreateOutputCachingApp(WebApplicationBuilder builder)
    {
        builder.Services.AddOutputCache(options => options.AddBasePolicy(policy => policy.UseResponseCachingSemantics()));

        var app = builder.Build();
        app.UseOutputCache();
        MapEndpoints(app);
        return app;
    }

    // After, complete migration: the header the response varies by is declared upfront, so it's part of the cache key.
    public WebApplication CreateOutputCachingWithVaryByHeaderApp(WebApplicationBuilder builder)
    {
        builder.Services.AddOutputCache(options => options.AddBasePolicy(policy => policy.UseResponseCachingSemantics().SetVaryByHeader("Accept-Language")));

        var app = builder.Build();
        app.UseOutputCache();
        MapEndpoints(app);
        return app;
    }

    public override void MapEndpoints(WebApplication app)
    {
        var counter = 0;
        app.MapGet("/", (HttpContext context) =>
        {
            var language = context.Request.Headers.AcceptLanguage.ToString() is "de" ? "de" : "en";

            context.Response.Headers.CacheControl = "public, max-age=10";
            context.Response.Headers.Vary = "Accept-Language";
            return $"{(language is "de" ? "Hallo" : "Hello")} #{Interlocked.Increment(ref counter)}";
        });
    }

    public override IReadOnlyList<Step> Steps =>
    [
        new("1. Accept-Language: de: endpoint runs", ConfigureRequest: AcceptLanguage("de")),
        new("2. Accept-Language: de: served from cache", ConfigureRequest: AcceptLanguage("de")),
        new("3. Accept-Language: en: endpoint runs", ConfigureRequest: AcceptLanguage("en")),
        new("4. Accept-Language: en: served from cache", ConfigureRequest: AcceptLanguage("en")),
        new("5. Accept-Language: de: served from cache", ConfigureRequest: AcceptLanguage("de")),
    ];

    public override IReadOnlyList<ScenarioApplication> Applications =>
    [
        .. base.Applications,
        new("varybyheader", "OC + policy + VaryBy", CreateOutputCachingWithVaryByHeaderApp),
    ];

    private static Action<System.Net.Http.HttpRequestMessage> AcceptLanguage(string language)
        => request => request.Headers.TryAddWithoutValidation("Accept-Language", language);
}
