#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net12.0
// Uses the Output Caching built from this repository, which contains UseResponseCachingSemantics().
#:project ../../src/Microsoft.AspNetCore.OutputCaching.csproj

// Scenario: the endpoint controls caching with the Cache-Control response header,
// e.g. "Cache-Control: public, max-age=3", which is also what [ResponseCache(Duration = 3)] writes in MVC.
//
//   dotnet run CacheControlHeaders.cs                        runs the setups side by side and compares their responses
//   dotnet run CacheControlHeaders.cs -- responsecaching     serves the "before" setup on http://localhost:5000
//   dotnet run CacheControlHeaders.cs -- outputcaching       serves the "after" setup on http://localhost:5000

switch (args.FirstOrDefault())
{
    case "responsecaching":
        CreateResponseCachingApp("http://localhost:5000").Run();
        break;
    case "outputcaching":
        CreateOutputCachingApp("http://localhost:5000").Run();
        break;
    default:
        await CompareAsync();
        break;
}

// Before: Response Caching.
static WebApplication CreateResponseCachingApp(string url)
{
    var builder = CreateBuilder(url);
    builder.Services.AddResponseCaching();

    var app = builder.Build();
    app.UseResponseCaching();
    MapEndpoint(app);
    return app;
}

// After: Output Caching with the Response Caching semantics. Only these two lines change.
static WebApplication CreateOutputCachingApp(string url)
{
    var builder = CreateBuilder(url);
    builder.Services.AddOutputCache(options => options.AddBasePolicy(policy => policy.UseResponseCachingSemantics()));

    var app = builder.Build();
    app.UseOutputCache();
    MapEndpoint(app);
    return app;
}

// For comparison: Output Caching with its default policy, which ignores the Cache-Control headers.
static WebApplication CreateDefaultOutputCachingApp(string url)
{
    var builder = CreateBuilder(url);
    builder.Services.AddOutputCache(options => options.AddBasePolicy(policy => policy.Cache()));

    var app = builder.Build();
    app.UseOutputCache();
    MapEndpoint(app);
    return app;
}

// The endpoint is identical in all setups. The counter shows whether the endpoint ran or the response came from the cache.
static void MapEndpoint(WebApplication app)
{
    var counter = 0;
    app.MapGet("/", (HttpContext context) =>
    {
        context.Response.Headers.CacheControl = "public, max-age=3";
        return $"response #{Interlocked.Increment(ref counter)}";
    });
}

static WebApplicationBuilder CreateBuilder(string url)
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls(url);
    return builder;
}

static async Task CompareAsync()
{
    const string beforeUrl = "http://localhost:5101";
    const string afterUrl = "http://localhost:5102";
    const string defaultUrl = "http://localhost:5103";

    await using var before = CreateResponseCachingApp(beforeUrl);
    await using var after = CreateOutputCachingApp(afterUrl);
    await using var outputCachingDefault = CreateDefaultOutputCachingApp(defaultUrl);
    await before.StartAsync();
    await after.StartAsync();
    await outputCachingDefault.StartAsync();

    var steps = new (string Description, string Path, string? RequestCacheControl, int DelaySeconds)[]
    {
        ("1. First request: the endpoint runs", "/", null, 0),
        ("2. Same request within max-age: served from cache", "/", null, 0),
        ("3. Client sends 'Cache-Control: no-cache': endpoint runs", "/", "no-cache", 0),
        ("4. Client sends 'Cache-Control: max-age=0': endpoint runs", "/", "max-age=0", 0),
        ("5. Same request: served from cache", "/", null, 0),
        ("6. Wait 4s, max-age=3 expired: endpoint runs", "/", null, 4),
        ("7. Known difference: query string is part of the key", "/?page=2", null, 0),
    };

    using var client = new HttpClient();

    Console.WriteLine($"{"Step",-62}{"ResponseCaching",-24}{"OutputCaching + policy",-24}{"OutputCaching default",-24}");
    foreach (var step in steps)
    {
        await Task.Delay(TimeSpan.FromSeconds(step.DelaySeconds));
        var beforeResult = await SendAsync(client, beforeUrl + step.Path, step.RequestCacheControl);
        var afterResult = await SendAsync(client, afterUrl + step.Path, step.RequestCacheControl);
        var defaultResult = await SendAsync(client, defaultUrl + step.Path, step.RequestCacheControl);
        Console.WriteLine($"{step.Description,-62}{beforeResult,-24}{afterResult,-24}{defaultResult,-24}");
    }
}

static async Task<string> SendAsync(HttpClient client, string url, string? requestCacheControl)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    if (requestCacheControl is not null)
    {
        request.Headers.TryAddWithoutValidation("Cache-Control", requestCacheControl);
    }

    using var response = await client.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    return response.Headers.Age is { } age ? $"{body} (Age: {age.TotalSeconds}s)" : body;
}
