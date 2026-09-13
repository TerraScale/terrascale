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
        await Assert.ThrowsAsync<ArgumentNullException>(() => Task.Run(() => EndpointDiscovery.Discover(null!)));
        var app = WebApplication.CreateBuilder().Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => new InvalidEndpoint().Map(app)));
        await app.DisposeAsync();
    }

    [Test]
    public async Task PrecomputedCatalogDeduplicatesTypes()
    {
        var catalog = new EndpointCatalog([typeof(ProbeEndpoint), typeof(ProbeEndpoint)]);
        await Assert.That(catalog.Count).IsEqualTo(1);
        await Assert.That(catalog.Types.Single()).IsEqualTo(typeof(ProbeEndpoint));
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
