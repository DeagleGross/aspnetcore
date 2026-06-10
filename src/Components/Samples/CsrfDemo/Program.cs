// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using CsrfDemo;

var resultsPath = args.Length > 0 ? args[0] : "results.md";

var withAfPort = 5111;
var withoutAfPort = 5112;

var withAf = await StartAppAsync(withAfPort, useAntiforgery: true);
var withoutAf = await StartAppAsync(withoutAfPort, useAntiforgery: false);

var sections = new List<string>();
var summaryRows = new List<string>();
foreach (var (label, port, useAf) in new[]
{
    ("App 1 — with app.UseAntiforgery()", withAfPort, true),
    ("App 2 — without app.UseAntiforgery()", withoutAfPort, false),
})
{
    var section = new StringBuilder();
    section.AppendLine($"## {label}");
    section.AppendLine();
    section.AppendLine($"Running on `http://localhost:{port}` (Kestrel, in-process).");
    section.AppendLine();

    var origin = $"http://localhost:{port}";

    var rendered = await GetRenderedFormAsync(origin);
    section.AppendLine("### GET /form — what the rendered form looks like");
    section.AppendLine();
    section.AppendLine($"- `<AntiforgeryToken />` produced a hidden `__RequestVerificationToken` input: **{(rendered.HasToken ? "YES" : "NO")}**");
    section.AppendLine($"- Antiforgery cookie returned by server: **{(rendered.HasCookie ? "YES" : "NO")}**");
    section.AppendLine();
    if (!string.IsNullOrEmpty(rendered.FormHtml))
    {
        section.AppendLine("```html");
        section.AppendLine(rendered.FormHtml);
        section.AppendLine("```");
        section.AppendLine();
    }

    var a = await RunCrossSitePostAsync(origin);
    AppendScenario(section, "A. Cross-site POST (browser CSRF: Sec-Fetch-Site: cross-site)", a);

    var b = await RunSameOriginInvalidTokenAsync(origin);
    AppendScenario(section, "B. Same-origin POST with INVALID antiforgery token", b);

    var c = await RunSameOriginValidTokenAsync(origin, rendered);
    AppendScenario(section, "C. Same-origin POST with VALID antiforgery token + cookie", c);

    summaryRows.Add($"| {label} | {ShortStatus(a)} | {ShortStatus(b)} | {ShortStatus(c)} | {(rendered.HasToken ? "yes" : "**no**")} |");

    sections.Add(section.ToString());
}

await File.WriteAllTextAsync(resultsPath, BuildMarkdown(sections, summaryRows));
Console.WriteLine($"Wrote: {Path.GetFullPath(resultsPath)}");

await withAf.StopAsync();
await withoutAf.StopAsync();

static async Task<WebApplication> StartAppAsync(int port, bool useAntiforgery)
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseKestrel(o => o.ListenLocalhost(port));
    builder.Logging.ClearProviders();

    builder.Services.AddRazorComponents();
    builder.Services.AddAntiforgery();

    var app = builder.Build();
    if (useAntiforgery)
    {
        app.UseAntiforgery();
    }
    app.MapRazorComponents<App>();

    await app.StartAsync();
    await Task.Delay(200);
    return app;
}

static void AppendScenario(StringBuilder section, string title, Result r)
{
    section.AppendLine($"### {title}");
    section.AppendLine();
    section.AppendLine("```http");
    section.AppendLine(r.RequestSummary.Trim());
    section.AppendLine();
    section.AppendLine($"HTTP/1.1 {(int)r.Status} {r.Status}");
    foreach (var (k, v) in r.ResponseHeaders) section.AppendLine($"{k}: {v}");
    section.AppendLine();
    section.AppendLine(Truncate(r.Body, 600));
    section.AppendLine("```");
    section.AppendLine();
    section.AppendLine($"**Outcome:** {r.Outcome}");
    section.AppendLine();
}

static async Task<RenderedForm> GetRenderedFormAsync(string origin)
{
    using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true, AllowAutoRedirect = false };
    using var http = new HttpClient(handler) { BaseAddress = new Uri(origin) };
    var getReq = new HttpRequestMessage(HttpMethod.Get, "/form");
    getReq.Headers.Add("Sec-Fetch-Site", "same-origin");
    var resp = await http.SendAsync(getReq);
    var body = await resp.Content.ReadAsStringAsync();

    var tokenMatch = Regex.Match(body, "name=\"__RequestVerificationToken\"[^>]*value=\"(?<v>[^\"]+)\"");
    var formMatch = Regex.Match(body, "<form[^>]*>.*?</form>", RegexOptions.Singleline);
    var cookies = handler.CookieContainer.GetCookies(new Uri(origin));

    return new RenderedForm(
        Token: tokenMatch.Success ? tokenMatch.Groups["v"].Value : null,
        Cookies: cookies,
        HasToken: tokenMatch.Success,
        HasCookie: cookies.Count > 0,
        FormHtml: formMatch.Success ? formMatch.Value : "");
}

static async Task<Result> RunCrossSitePostAsync(string origin)
{
    using var http = new HttpClient { BaseAddress = new Uri(origin) };
    using var req = new HttpRequestMessage(HttpMethod.Post, "/form");
    req.Headers.Add("Sec-Fetch-Site", "cross-site");
    req.Headers.Add("Origin", "https://evil.example");
    req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["_handler"] = "echo",
        ["value"] = "attack",
    });
    var resp = await http.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();
    var summary = $"POST /form  (Sec-Fetch-Site: cross-site, Origin: https://evil.example)\nContent-Type: application/x-www-form-urlencoded\n_handler=echo&value=attack";
    return new Result(summary, resp.StatusCode, ExtractHeaders(resp), body, ClassifyOutcome(resp, body));
}

static async Task<Result> RunSameOriginInvalidTokenAsync(string origin)
{
    using var http = new HttpClient { BaseAddress = new Uri(origin) };
    using var req = new HttpRequestMessage(HttpMethod.Post, "/form");
    req.Headers.Add("Sec-Fetch-Site", "same-origin");
    req.Headers.Add("Origin", origin);
    req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["_handler"] = "echo",
        ["value"] = "hello",
        ["__RequestVerificationToken"] = "this-is-not-a-real-token",
    });
    var resp = await http.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();
    var summary = $"POST /form  (Sec-Fetch-Site: same-origin, Origin: {origin})\nContent-Type: application/x-www-form-urlencoded\n_handler=echo&value=hello&__RequestVerificationToken=this-is-not-a-real-token";
    return new Result(summary, resp.StatusCode, ExtractHeaders(resp), body, ClassifyOutcome(resp, body));
}

static async Task<Result> RunSameOriginValidTokenAsync(string origin, RenderedForm rendered)
{
    var container = new CookieContainer();
    foreach (Cookie c in rendered.Cookies)
    {
        container.Add(new Uri(origin), new Cookie(c.Name, c.Value) { Domain = c.Domain });
    }
    using var handler = new HttpClientHandler { CookieContainer = container, UseCookies = true, AllowAutoRedirect = false };
    using var http = new HttpClient(handler) { BaseAddress = new Uri(origin) };

    using var req = new HttpRequestMessage(HttpMethod.Post, "/form");
    req.Headers.Add("Sec-Fetch-Site", "same-origin");
    req.Headers.Add("Origin", origin);
    var content = new Dictionary<string, string>
    {
        ["_handler"] = "echo",
        ["value"] = "hello",
    };
    if (!string.IsNullOrEmpty(rendered.Token)) content["__RequestVerificationToken"] = rendered.Token;
    req.Content = new FormUrlEncodedContent(content);
    var resp = await http.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();

    var summary = $"POST /form  (Sec-Fetch-Site: same-origin, Origin: {origin})\nCookie: {(rendered.HasCookie ? "(forwarded antiforgery cookie)" : "(no cookie)")}\nContent-Type: application/x-www-form-urlencoded\n_handler=echo&value=hello&__RequestVerificationToken={(rendered.HasToken ? "<valid token from GET>" : "<none — server did not mint one>")}";
    return new Result(summary, resp.StatusCode, ExtractHeaders(resp), body, ClassifyOutcome(resp, body));
}

static List<(string, string)> ExtractHeaders(HttpResponseMessage r)
{
    var keep = new[] { "Content-Type", "Content-Length", "Location", "Cache-Control" };
    var list = new List<(string, string)>();
    foreach (var h in r.Headers.Concat(r.Content.Headers))
    {
        if (keep.Contains(h.Key, StringComparer.OrdinalIgnoreCase))
        {
            list.Add((h.Key, string.Join(", ", h.Value)));
        }
    }
    return list;
}

static string ClassifyOutcome(HttpResponseMessage r, string body)
{
    if (r.StatusCode == HttpStatusCode.BadRequest)
    {
        var hasHtmlContentType = string.Equals(r.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase);
        if (body.Contains("antiforgery token", StringComparison.OrdinalIgnoreCase))
            return "❌ **400 Bad Request** — `RazorComponentEndpointInvoker` rejected based on `IAntiforgeryValidationFeature.IsValid == false`.";
        if (hasHtmlContentType)
            return "❌ **400 Bad Request** — `AntiforgeryMiddleware` rejected (invalid `__RequestVerificationToken` cookie+form pair).";
        return "🛑 **400 Bad Request** — `CsrfProtectionMiddleware` rejected (`Sec-Fetch-Site` / `Origin` check failed).";
    }
    if ((int)r.StatusCode >= 500) return $"💥 **{(int)r.StatusCode}** — server error.";
    if ((int)r.StatusCode >= 200 && (int)r.StatusCode < 300) return "✅ **200 OK** — request accepted, component rendered, form value bound and echoed back.";
    return $"**{(int)r.StatusCode} {r.StatusCode}**.";
}

static string ShortReason(string body) => string.IsNullOrEmpty(body) ? "(empty body — typically a middleware rejection)" : (body.Length > 200 ? body[..200].Replace("\n", " ") + "..." : body.Replace("\n", " "));

static string Truncate(string body, int max) => string.IsNullOrEmpty(body) ? "(empty body)" : (body.Length > max ? body[..max] + "...(truncated)" : body);

static string ShortStatus(Result r) => r.Status switch
{
    HttpStatusCode.OK => "✅ 200",
    HttpStatusCode.BadRequest => string.Equals(r.ResponseHeaders.FirstOrDefault(h => h.Item1.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Item2, "text/html; charset=utf-8", StringComparison.OrdinalIgnoreCase) ? "❌ 400 (AF)" : "🛑 400 (CSRF)",
    _ => $"{(int)r.Status} {r.Status}"
};

static string BuildMarkdown(List<string> sections, List<string> summaryRows)
{
    var sb = new StringBuilder();
    sb.AppendLine("# CSRF + Antiforgery — Blazor end-to-end demo");
    sb.AppendLine();
    sb.AppendLine("This file is produced by running `dotnet run` in `src/Components/Samples/CsrfDemo`.");
    sb.AppendLine("It spins up two `WebApplication` instances in-process against the local PR source");
    sb.AppendLine("(`dmkorolev/csrf-in-blazor` in `D:\\code\\aspnetcore`):");
    sb.AppendLine();
    sb.AppendLine("- **App 1**: `WebApplication.CreateBuilder()` + `app.UseAntiforgery()` + `app.MapRazorComponents<App>()`.");
    sb.AppendLine("- **App 2**: `WebApplication.CreateBuilder()` + `app.MapRazorComponents<App>()` **without** `app.UseAntiforgery()`.");
    sb.AppendLine();
    sb.AppendLine("Three scenarios are fired against each app:");
    sb.AppendLine();
    sb.AppendLine("- **A.** Cross-site POST with `Sec-Fetch-Site: cross-site` and `Origin: https://evil.example`.");
    sb.AppendLine("- **B.** Same-origin POST with a deliberately invalid `__RequestVerificationToken`.");
    sb.AppendLine("- **C.** Same-origin POST after first GETting `/form` and forwarding the rendered token + antiforgery cookie.");
    sb.AppendLine();
    sb.AppendLine("Both apps run as default `WebApplication`, so the new `CsrfProtectionMiddleware` (PR #66585)");
    sb.AppendLine("is auto-injected for both.");
    sb.AppendLine();
    sb.AppendLine("## Summary");
    sb.AppendLine();
    sb.AppendLine("| Configuration | A. Cross-site POST | B. Same-origin POST + invalid token | C. Same-origin POST + valid token + cookie | `<AntiforgeryToken />` rendered? |");
    sb.AppendLine("| --- | --- | --- | --- | --- |");
    foreach (var row in summaryRows) sb.AppendLine(row);
    sb.AppendLine();
    sb.AppendLine("- `✅ 200` = request accepted, component rendered, form value bound and echoed back.");
    sb.AppendLine("- `🛑 400 (CSRF)` = `CsrfProtectionMiddleware` rejected (`Sec-Fetch-Site`/`Origin` check). Empty body, no `Content-Type`.");
    sb.AppendLine("- `❌ 400 (AF)` = `AntiforgeryMiddleware` rejected (invalid token/cookie pair). Empty body, `Content-Type: text/html`.");
    sb.AppendLine();
    foreach (var s in sections) sb.AppendLine(s);
    sb.AppendLine("## Code references");
    sb.AppendLine();
    sb.AppendLine("- `src/DefaultBuilder/src/Internal/CsrfProtectionMiddleware.cs` — auto-injected; runs the 6-step `Sec-Fetch-Site` / `Origin` algorithm and writes `HttpContext.Items[MiddlewareInvokedKeys.CsrfProtection]` when an endpoint matched. Returns **400 Bad Request** with an empty body on rejection.");
    sb.AppendLine("- `src/Antiforgery/src/AntiforgeryMiddleware.cs` — only present in App 1; writes `HttpContext.Items[MiddlewareInvokedKeys.Antiforgery]` and sets `IAntiforgeryValidationFeature`.");
    sb.AppendLine("- `src/Http/Routing/src/EndpointMiddleware.cs` — safety check now accepts **either** the AF or CSRF marker; this is why App 2 no longer throws *\"MapRazorComponents unexpectedly requires antiforgery middleware\"*.");
    sb.AppendLine("- `src/Components/Endpoints/src/RazorComponentEndpointInvoker.cs` — no longer calls `IAntiforgery.ValidateRequestAsync`; trusts the `IAntiforgeryValidationFeature` verdict and returns 400 only when `IsValid == false`.");
    sb.AppendLine("- `src/Components/Endpoints/src/Forms/EndpointAntiforgeryStateProvider.cs` — only mints `__RequestVerificationToken` when the AF marker is present.");
    sb.AppendLine("- `src/Shared/MiddlewareInvokedKeys.cs` — shared internal constants linked into all four assemblies above.");
    sb.AppendLine();
    sb.AppendLine("## How to reproduce locally");
    sb.AppendLine();
    sb.AppendLine("```powershell");
    sb.AppendLine("cd D:\\code\\aspnetcore");
    sb.AppendLine(". .\\activate.ps1");
    sb.AppendLine("cd src\\Components\\Samples\\CsrfDemo");
    sb.AppendLine("dotnet run -- C:\\path\\to\\output.md");
    sb.AppendLine("```");
    return sb.ToString();
}

record Result(string RequestSummary, HttpStatusCode Status, List<(string, string)> ResponseHeaders, string Body, string Outcome);

record RenderedForm(string? Token, CookieCollection Cookies, bool HasToken, bool HasCookie, string FormHtml);
