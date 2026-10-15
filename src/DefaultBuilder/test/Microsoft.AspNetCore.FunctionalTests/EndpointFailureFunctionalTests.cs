// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.Tests;

public class EndpointFailureFunctionalTests : LoggedTest
{
    [Theory]
    [InlineData(false, false, false, "minimal")]
    [InlineData(false, true, false, "minimal")]
    [InlineData(true, false, false, "minimal")]
    [InlineData(true, true, false, "minimal")]
    [InlineData(false, false, true, "minimal")]
    [InlineData(true, true, true, "minimal")]
    [InlineData(false, false, false, "authorization")]
    [InlineData(true, true, true, "authorization")]
    [InlineData(false, false, false, "controllers")]
    [InlineData(true, true, true, "controllers")]
    public async Task InvalidJsonEndpointDoesNotPreventHealthyEndpointsFromRunning(
        bool grouped, bool explicitRouting, bool response, string services)
    {
        var builder = CreateBuilder();
        if (services == "authorization")
        {
            builder.Services.AddAuthorization();
        }
        else if (services == "controllers")
        {
            builder.Services.AddControllers();
        }

        await using var app = builder.Build();
        var errors = ObserveErrors(app);
        app.MapGet("/ok", () => "ok");
        if (explicitRouting)
        {
            app.UseRouting();
        }

        IEndpointRouteBuilder endpoints = grouped ? app.MapGroup("/group") : app;
        var initialized = 0;
        MapInvalidEndpoint(endpoints, response).Add(_ => initialized++);
        endpoints.MapGet("/after", () => "after");

        await app.StartAsync();

        Assert.Equal(services == "minimal" ? 0 : 1, initialized);
        Assert.Equal(services == "minimal" ? 0 : 1, FailureLogs().Count());
        using var client = CreateClient(app);
        Assert.Equal("ok", await client.GetStringAsync("/ok"));
        var materializations = services == "minimal" ? 1 : 2;
        Assert.Equal(materializations, initialized);
        Assert.Equal(materializations, FailureLogs().Count());
        var log = FailureLogs().Last();
        Assert.Equal(LogLevel.Error, log.LogLevel);
        Assert.Contains("invalid endpoint", log.Message);
        var original = Assert.IsType<NotSupportedException>(log.Exception);
        Assert.Contains(nameof(InvalidApproval), original.Message);
        Assert.Contains(nameof(JsonUnionTypeStructuralClassifier), original.Message);

        var prefix = grouped ? "/group" : "";
        Assert.Equal("after", await client.GetStringAsync(prefix + "/after"));
        for (var i = 0; i < 2; i++)
        {
            using var result = response
                ? await client.GetAsync(prefix + "/invalid")
                : await client.PostAsync(prefix + "/invalid", Json("{}"));
            Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
            var failure = Assert.Single(errors);
            Assert.True(errors.TryDequeue(out _));
            Assert.Same(original, failure.Exception);
            Assert.Contains("RequestDelegateFactory", failure.Exception.StackTrace);
            Assert.Equal("invalid endpoint", failure.Endpoint.DisplayName);
            Assert.Equal("invalid", failure.Endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
            Assert.NotNull(failure.Endpoint.Metadata.GetMetadata<FinallyMetadata>());
        }

        Assert.Equal("ok", await client.GetStringAsync("/ok"));
        Assert.Equal(materializations, FailureLogs().Count());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task DynamicInvalidEndpointDoesNotBreakExistingOrNewHealthyEndpoints(
        bool grouped, bool beforeFirstRequest, bool authorization)
    {
        var builder = CreateBuilder();
        if (authorization)
        {
            builder.Services.AddAuthorization();
        }

        await using var app = builder.Build();
        var errors = ObserveErrors(app);
        app.MapGet("/ok", () => "ok");
        var source = new MutableRouteDataSource(app.Services);
        ((IEndpointRouteBuilder)app).DataSources.Add(source);
        source.Update(endpoints => endpoints.MapGet("/existing", () => "existing"));
        await app.StartAsync();
        using var client = CreateClient(app);
        Assert.Equal(authorization ? 1 : 0, source.EnumerationCount);

        if (!beforeFirstRequest)
        {
            Assert.Equal("ok", await client.GetStringAsync("/ok"));
            Assert.Equal("existing", await client.GetStringAsync("/existing"));
        }

        source.Update(endpoints =>
        {
            endpoints.MapGet("/existing", () => "existing");
            IEndpointRouteBuilder routes = grouped ? endpoints.MapGroup("/group") : endpoints;
            MapInvalidEndpoint(routes, response: false);
            routes.MapGet("/new", () => "new");
        });

        Assert.Equal("ok", await client.GetStringAsync("/ok"));
        Assert.Equal("existing", await client.GetStringAsync("/existing"));
        var prefix = grouped ? "/group" : "";
        Assert.Equal("new", await client.GetStringAsync(prefix + "/new"));
        var materializations = authorization ? 2 : 1;
        Assert.Equal(materializations, FailureLogs().Count());
        var log = FailureLogs().Last();
        using var invalid = await client.PostAsync(prefix + "/invalid", Json("{}"));
        Assert.Equal(HttpStatusCode.InternalServerError, invalid.StatusCode);
        Assert.Same(log.Exception, Assert.Single(errors).Exception);
        Assert.Equal("ok", await client.GetStringAsync("/ok"));

        source.Update(endpoints =>
        {
            endpoints.MapGet("/existing", () => "existing");
            endpoints.MapPost(prefix + "/invalid", () => "repaired");
            endpoints.MapGet(prefix + "/new", () => "new");
        });

        using var repaired = await client.PostAsync(prefix + "/invalid", Json("{}"));
        Assert.Equal(HttpStatusCode.OK, repaired.StatusCode);
        Assert.Equal("repaired", await repaired.Content.ReadAsStringAsync());
        Assert.Equal("new", await client.GetStringAsync(prefix + "/new"));
        Assert.Equal("ok", await client.GetStringAsync("/ok"));
        Assert.Equal(materializations, FailureLogs().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadTimeJsonErrorsRemainBadRequests(bool grouped)
    {
        var builder = CreateBuilder();
        await using var app = builder.Build();
        app.MapGet("/ok", () => "ok");
        IEndpointRouteBuilder routes = grouped ? app.MapGroup("/group") : app;
        routes.MapPost("/ambiguous", (AmbiguousApproval approval) => approval);
        routes.MapPost("/scalar", (Scalar scalar) => scalar);
        await app.StartAsync();
        using var client = CreateClient(app);
        var prefix = grouped ? "/group" : "";

        Assert.Equal("ok", await client.GetStringAsync("/ok"));
        using var ambiguous = await client.PostAsync(prefix + "/ambiguous", Json("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);
        using var malformed = await client.PostAsync(prefix + "/ambiguous", Json("{"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var valid = await client.PostAsync(prefix + "/scalar", Json("42"));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal("42", await valid.Content.ReadAsStringAsync());
        Assert.Empty(FailureLogs());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidPolymorphicJsonConfigurationIsIsolated(bool response)
    {
        var builder = CreateBuilder();
        await using var app = builder.Build();
        var errors = ObserveErrors(app);
        if (response)
        {
            app.MapGet("/invalid", () => new InvalidPolymorphicType());
        }
        else
        {
            app.MapPost("/invalid", (InvalidPolymorphicType value) => "invalid handler must not run");
        }
        app.MapGet("/ok", () => "ok");
        await app.StartAsync();
        using var client = CreateClient(app);

        Assert.Empty(FailureLogs());
        Assert.Equal("ok", await client.GetStringAsync("/ok"));
        var original = Assert.IsType<InvalidOperationException>(Assert.Single(FailureLogs()).Exception);
        Assert.Contains(nameof(InvalidPolymorphicType), original.Message);
        using var invalid = response
            ? await client.GetAsync("/invalid")
            : await client.PostAsync("/invalid", Json("{}"));
        Assert.Equal(HttpStatusCode.InternalServerError, invalid.StatusCode);
        Assert.Same(original, Assert.Single(errors).Exception);
        Assert.Equal("ok", await client.GetStringAsync("/ok"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndpointFilterFactoryFailureIsIsolated(bool requestDelegate)
    {
        var builder = CreateBuilder();
        await using var app = builder.Build();
        var errors = ObserveErrors(app);
        var original = new InvalidOperationException("Filter factory failed.");
        var route = requestDelegate
            ? app.MapGet("/invalid", (HttpContext context) => context.Response.WriteAsync("invalid handler must not run"))
            : app.MapGet("/invalid", () => "invalid handler must not run");
        route.AddEndpointFilterFactory((_, _) => throw original);
        app.MapGet("/ok", () => "ok");
        await app.StartAsync();
        using var client = CreateClient(app);

        Assert.Equal("ok", await client.GetStringAsync("/ok"));
        Assert.Same(original, Assert.Single(FailureLogs()).Exception);
        using var invalid = await client.GetAsync("/invalid");
        Assert.Equal(HttpStatusCode.InternalServerError, invalid.StatusCode);
        Assert.Same(original, Assert.Single(errors).Exception);
        Assert.Equal("ok", await client.GetStringAsync("/ok"));
    }

    private WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(LoggerFactory);
        return builder;
    }

    private IEnumerable<WriteContext> FailureLogs() =>
        TestSink.Writes.Where(log => log.EventId.Name == "RequestDelegateCreationFailed");

    private static HttpClient CreateClient(WebApplication app) =>
        new() { BaseAddress = new Uri(Assert.Single(app.Urls)) };

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private static RouteHandlerBuilder MapInvalidEndpoint(IEndpointRouteBuilder endpoints, bool response)
    {
        var route = response
            ? endpoints.MapGet("/invalid", () => (InvalidApproval)new NotRequired())
            : endpoints.MapPost("/invalid", (InvalidApproval approval) => "invalid handler must not run");
        route.WithDisplayName("invalid endpoint").WithName("invalid");
        route.Finally(builder => builder.Metadata.Add(new FinallyMetadata()));
        return route;
    }

    private static ConcurrentQueue<(Exception Exception, Endpoint Endpoint)> ObserveErrors(WebApplication app)
    {
        var errors = new ConcurrentQueue<(Exception, Endpoint)>();
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception exception)
            {
                errors.Enqueue((exception, context.GetEndpoint()));
                throw;
            }
        });
        return errors;
    }

    public record NotRequired;
    public record PendingApproval;
    public record PartlyApproved(Guid Approver);

    [JsonUnion(TypeClassifier = typeof(JsonUnionTypeStructuralClassifier))]
    public union InvalidApproval(NotRequired, PendingApproval, PartlyApproved);

    public union AmbiguousApproval(NotRequired, PendingApproval, PartlyApproved);
    public union Scalar(int, string);

    [JsonPolymorphic]
    [JsonDerivedType(typeof(string))]
    public class InvalidPolymorphicType;

    private sealed class FinallyMetadata;

    private sealed class MutableRouteDataSource(IServiceProvider services) : EndpointDataSource, IDisposable
    {
        private RouteBuilder _routes = new(services);
        private CancellationTokenSource _change = new();

        public int EnumerationCount { get; private set; }

        public override IReadOnlyList<Endpoint> Endpoints
        {
            get
            {
                EnumerationCount++;
                return _routes.DataSources.SelectMany(source => source.Endpoints).ToArray();
            }
        }

        public override IChangeToken GetChangeToken() => new CancellationChangeToken(_change.Token);

        public void Update(Action<IEndpointRouteBuilder> configure)
        {
            var routes = new RouteBuilder(services);
            configure(routes);
            _routes = routes;
            var previous = _change;
            _change = new();
            previous.Cancel();
            previous.Dispose();
        }

        public void Dispose() => _change.Dispose();

        private sealed class RouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
        {
            public IServiceProvider ServiceProvider => services;
            public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
            public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(services);
        }
    }
}
