// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http;

namespace ResponseCachingCompatibility;

internal static class ScenarioRunner
{
    private const string ServeUrl = "http://localhost:5000";

    public static async Task ServeAsync(Func<WebApplicationBuilder, WebApplication> createApp)
    {
        await using var app = createApp(CreateBuilder(ServeUrl));
        await app.StartAsync();

        Console.WriteLine($"Listening on {ServeUrl}. Press Ctrl+C to stop.");
        await app.WaitForShutdownAsync();
    }

    public static async Task CompareAsync(Scenario scenario)
    {
        var applications = new (string Name, string Url, Func<WebApplicationBuilder, WebApplication> Create)[]
        {
            ("ResponseCaching", "http://localhost:5101", scenario.CreateResponseCachingApp),
            ("OutputCaching + policy", "http://localhost:5102", scenario.CreateOutputCachingApp),
            ("OutputCaching default", "http://localhost:5103", scenario.CreateDefaultOutputCachingApp),
        };

        var apps = new List<WebApplication>();
        try
        {
            foreach (var application in applications)
            {
                var app = application.Create(CreateBuilder(application.Url));
                apps.Add(app);
                await app.StartAsync();
            }

            Console.WriteLine($"{scenario.Name}: {scenario.Description}");
            Console.WriteLine();

            var descriptionWidth = scenario.Steps.Max(step => step.Description.Length) + 2;
            const int columnWidth = 26;

            Console.WriteLine("Step".PadRight(descriptionWidth) + string.Concat(applications.Select(a => a.Name.PadRight(columnWidth))));

            using var client = new HttpClient();
            foreach (var step in scenario.Steps)
            {
                await Task.Delay(TimeSpan.FromSeconds(step.DelaySeconds));

                var line = step.Description.PadRight(descriptionWidth);
                foreach (var application in applications)
                {
                    line += (await SendAsync(client, application.Url, step)).PadRight(columnWidth);
                }
                Console.WriteLine(line);
            }
        }
        finally
        {
            foreach (var app in apps)
            {
                await app.DisposeAsync();
            }
        }
    }

    private static async Task<string> SendAsync(HttpClient client, string baseUrl, Step step)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + step.Path);
        step.ConfigureRequest?.Invoke(request);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            body = $"{(int)response.StatusCode} {body}";
        }

        return response.Headers.Age is { } age ? $"{body} (Age: {age.TotalSeconds}s)" : body;
    }

    private static WebApplicationBuilder CreateBuilder(string url)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        return builder;
    }
}
