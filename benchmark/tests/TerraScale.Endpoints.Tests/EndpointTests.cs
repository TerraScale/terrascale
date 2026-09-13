using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Endpoints;

namespace TerraScale.Endpoints.Tests;

public sealed class EndpointTests
{
    [Test]
    public async Task DiscoveryIsCachedAndDeterministic()
    {
        var first = EndpointDiscovery.Discover(typeof(EndpointTests).Assembly);
        var second = EndpointDiscovery.Discover(typeof(EndpointTests).Assembly);
        await Assert.That(first.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(first.Select(t => t.FullName).ToArray()).IsEquivalentTo(first.Select(t => t.FullName).OrderBy(t => t, StringComparer.Ordinal).ToArray());
    }

    [Test]
    public async Task ConcurrentDiscoveryReturnsOnePublishedSnapshot()
    {
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => EndpointDiscovery.Discover(typeof(EndpointTests).Assembly))));
        var first = snapshots[0];
        await Assert.That(snapshots.All(snapshot => ReferenceEquals(first, snapshot))).IsTrue();
    }

    [Test]
    public async Task FastStyleEndpointMapsMetadata()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new EndpointCatalog([typeof(ProbeEndpoint)]));
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
    public async Task InvalidDefinitionAndArgumentsFailFast()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => new EndpointDefinition().Get("widgets")));
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => new EndpointDefinition().Method(" ", "/widgets")));
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => new EndpointDefinition().Verbs()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Task.Run(() => new EndpointDefinition().Produces<string>(600)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Task.Run(() => EndpointDiscovery.Discover(null!)));
        var app = WebApplication.CreateBuilder().Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => new InvalidEndpoint().Map(app)));
        await app.DisposeAsync();
    }

    [Test]
    public async Task FluentVerbsNormalizeAndDeduplicate()
    {
        var definition = new EndpointDefinition().WithRoute("/widgets").Verbs("get", "GET", "post");
        await Assert.That(definition.Route).IsEqualTo("/widgets");
        await Assert.That(definition.Methods).IsEquivalentTo(new[] { "GET", "POST" });
    }

    [Test]
    public async Task PrecomputedCatalogDeduplicatesTypes()
    {
        var catalog = new EndpointCatalog([typeof(ProbeEndpoint), typeof(ProbeEndpoint)]);
        await Assert.That(catalog.Count).IsEqualTo(1);
        await Assert.That(catalog.Types.Single()).IsEqualTo(typeof(ProbeEndpoint));
    }

    [Test]
    public async Task SenderStyleEndpointMapsAsARequestHandler()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(new EndpointCatalog([typeof(SenderProbeEndpoint)]));
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
}

public sealed class ProbeEndpoint : EndpointWithoutRequest<string>
{
    public override void Configure() { Get("/probe"); Name("probe"); Summary("Probe"); Tags("Tests"); AllowAnonymous(); }
    public override Task<string> HandleAsync(CancellationToken cancellationToken) => Task.FromResult("ok");
}
public sealed class InvalidEndpoint : EndpointWithoutRequest<string>
{
    public override void Configure() { }
    public override Task<string> HandleAsync(CancellationToken cancellationToken) => Task.FromResult("x");
}

public sealed class SenderProbeEndpoint : Endpoint<SenderProbeRequest>
{
    public override void Configure() { Post("/sender-probe"); AllowAnonymous(); Produces<SenderProbeResponse>(); }
    public override async Task HandleAsync(SenderProbeRequest request, CancellationToken cancellationToken)
    {
        await Send.OkAsync(new SenderProbeResponse(request.Value));
    }
}
public sealed record SenderProbeRequest(string Value);
public sealed record SenderProbeResponse(string Value);
