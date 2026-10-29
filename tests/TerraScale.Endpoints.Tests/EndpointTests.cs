using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;

namespace TerraScale.Endpoints.Tests;

public sealed class EndpointTests
{
    private static readonly string[] ExpectedMethods = ["GET", "POST"];

    [Test]
    public async Task DiscoveryIsCachedAndDeterministic()
    {
        var first = EndpointDiscovery.Discover(typeof(EndpointTests).Assembly);
        var second = EndpointDiscovery.Discover(typeof(EndpointTests).Assembly);
        await Assert.That(first.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        var names = first.Select(t => t.FullName).ToArray();
        await Assert.That(names).IsEquivalentTo(names.OrderBy(t => t, StringComparer.Ordinal).ToArray());
    }

    [Test]
    public async Task FastStyleEndpointMapsMetadata()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints([EndpointRegistration.Create<ProbeEndpoint>()]);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        await app.StartAsync();
        var endpoint = app.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).Single();
        await Assert.That(endpoint.Metadata.GetMetadata<RouteNameMetadata>()?.RouteName).IsEqualTo("probe");
        await Assert.That(endpoint.DisplayName).Contains("/probe");
        await app.StopAsync();
        await app.DisposeAsync();
    }

    [Test]
    public async Task FluentVerbsNormalizeAndDeduplicate()
    {
        var definition = new EndpointDefinition().WithRoute("/widgets").Verbs("get", "GET", "post");
        await Assert.That(definition.Route).IsEqualTo("/widgets");
        await Assert.That(definition.Methods).IsEquivalentTo(ExpectedMethods);
    }

    [Test]
    public async Task MultipleAcceptedContentTypesAreMapped()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints([EndpointRegistration.Create<RequestProbeEndpoint>()]);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        await app.StartAsync();
        var endpoint = app.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).Single();
        var accepted = endpoint.Metadata.GetOrderedMetadata<IAcceptsMetadata>();
        await Assert.That(accepted.SelectMany(item => item.ContentTypes)).Contains("application/json");
        await Assert.That(accepted.SelectMany(item => item.ContentTypes)).Contains("application/problem+json");
        await app.StopAsync();
        await app.DisposeAsync();
    }

    [Test]
    public async Task CatalogDeduplicatesAndMergesRegistrations()
    {
        var services = new ServiceCollection();
        services.AddTerraScaleEndpoints([EndpointRegistration.Create<ProbeEndpoint>(), EndpointRegistration.Create<ProbeEndpoint>()]);
        services.AddTerraScaleEndpoints([EndpointRegistration.Create<ProbeEndpoint>()]);
        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<EndpointCatalog>();
        await Assert.That(catalog.Count).IsEqualTo(1);
        await Assert.That(catalog.Types.Single()).IsEqualTo(typeof(ProbeEndpoint));
    }

    [Test]
    public async Task SenderStyleEndpointMapsAsARequestHandler()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints([EndpointRegistration.Create<SenderProbeEndpoint>()]);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        await app.StartAsync();
        var endpoint = app.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .Single();
        await Assert.That(endpoint.DisplayName).Contains("/sender-probe");
        await app.StopAsync();
        await app.DisposeAsync();
    }

    [Test]
    public async Task ScopedEndpointDependenciesResolvePerRequest()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.UseDefaultServiceProvider(options => options.ValidateScopes = true);
        builder.Services.AddScoped<ScopedProbeDependency>();
        builder.Services.AddTerraScaleEndpoints([typeof(ScopedProbeEndpoint)]);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var first = await client.GetStringAsync("/scoped-probe");
        var second = await client.GetStringAsync("/scoped-probe");

        await Assert.That(first).IsNotEqualTo(second);
        await app.StopAsync();
        await app.DisposeAsync();
    }

    [Test]
    public async Task CustomOpenApiDocumentAndScalarRoutesUseConfiguredDocument()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints([typeof(ProbeEndpoint)], options =>
        {
            options.Title = "Custom TerraScale API";
            options.Version = "v2";
            options.OpenApiRoute = "/docs/{documentName}.json";
            options.ScalarRoute = "/reference";
        });
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var document = JsonDocument.Parse(await client.GetStringAsync("/docs/v2.json"));
        var scalar = await client.GetStringAsync("/reference/");

        await Assert.That(document.RootElement.GetProperty("info").GetProperty("title").GetString()).IsEqualTo("Custom TerraScale API");
        await Assert.That(document.RootElement.GetProperty("info").GetProperty("version").GetString()).IsEqualTo("v2");
        await Assert.That(scalar).Contains("\"url\":\"docs/v2.json\"");
        await Assert.That(scalar).Contains("\"withDefaultFonts\":false");
        await Assert.That(scalar).Contains("\"telemetry\":false");
        await Assert.That(scalar).Contains("\"agent\":{\"disabled\":true}");

        await app.StopAsync();
        await app.DisposeAsync();
    }

    [Test]
    public async Task ConventionsOnMappedDocsReachOpenApiAndScalar()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints([typeof(ProbeEndpoint)]);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi().WithMetadata(new ListenerMarker());
        await app.StartAsync();

        var endpoints = app.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).ToArray();
        static bool HasPattern(Endpoint endpoint, string text) => endpoint is RouteEndpoint route && route.RoutePattern.RawText?.Contains(text, StringComparison.Ordinal) == true;
        var docs = endpoints.Where(endpoint => HasPattern(endpoint, "/openapi") || HasPattern(endpoint, "/scalar")).ToArray();
        await Assert.That(docs.Any(endpoint => HasPattern(endpoint, "/openapi"))).IsTrue();
        await Assert.That(docs.Any(endpoint => HasPattern(endpoint, "/scalar"))).IsTrue();
        await Assert.That(docs.All(endpoint => endpoint.Metadata.GetMetadata<ListenerMarker>() is not null)).IsTrue();
        var probe = endpoints.SingleOrDefault(endpoint => HasPattern(endpoint, "/probe"));
        await Assert.That(probe).IsNotNull();
        await Assert.That(probe!.Metadata.GetMetadata<ListenerMarker>()).IsNull();

        await app.StopAsync();
        await app.DisposeAsync();
    }

    private sealed record ListenerMarker;

    [Test]
    public async Task EndpointLivesThroughResultExecution()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints([typeof(DeferredResultEndpoint)]);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var body = await client.GetStringAsync("/deferred-result");

        await Assert.That(body).IsEqualTo("alive");
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

public sealed class ProbeEndpoint : EndpointWithoutRequest<string>
{
    public override void Configure()
    {
        Get("/probe");
        Name("probe");
        Summary("Probe");
        Tags("Tests");
        AllowAnonymous();
    }

    public override ValueTask<string> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult("ok");
}

public sealed class InvalidEndpoint : EndpointWithoutRequest<string>
{
    public override void Configure() { }
    public override ValueTask<string> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult("x");
}

public sealed class SenderProbeEndpoint : Endpoint<SenderProbeRequest>
{
    public override void Configure()
    {
        Post("/sender-probe");
        AllowAnonymous();
        Produces<SenderProbeResponse>();
    }

    public override async ValueTask HandleAsync(SenderProbeRequest request, CancellationToken cancellationToken)
    {
        await Send.OkAsync(new SenderProbeResponse(request.Value), cancellationToken);
    }
}

public sealed record SenderProbeRequest(string Value);
public sealed record SenderProbeResponse(string Value);

public sealed class RequestProbeEndpoint : Endpoint<RequestProbe, string>
{
    public override void Configure()
    {
        Post("/request-probe");
        Accepts<RequestProbe>("application/json", "application/problem+json");
    }

    public override ValueTask<string> HandleAsync(RequestProbe request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.Value);
}

public sealed record RequestProbe(string Value);

public sealed class ScopedProbeDependency
{
    public Guid Id { get; } = Guid.NewGuid();
}

public sealed class ScopedProbeEndpoint(ScopedProbeDependency dependency) : EndpointWithoutRequest<string>
{
    public override void Configure() => Get("/scoped-probe");

    public override ValueTask<string> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult(dependency.Id.ToString());
}

public sealed class DeferredResultEndpoint : EndpointWithoutRequest<IResult>, IAsyncDisposable
{
    private bool disposed;

    public override void Configure() => Get("/deferred-result");

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IResult>(new DeferredResult(this));

    public ValueTask DisposeAsync()
    {
        this.disposed = true;
        return ValueTask.CompletedTask;
    }

    private sealed class DeferredResult(DeferredResultEndpoint endpoint) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = "text/plain";
            await httpContext.Response.WriteAsync(endpoint.disposed ? "disposed" : "alive");
        }
    }
}
