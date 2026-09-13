using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
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
        await Assert.That(first.Select(type => type.FullName).ToArray())
            .IsEquivalentTo(first.Select(type => type.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Test]
    public async Task CatalogCanUsePrecomputedTypesWithoutScanning()
    {
        var catalog = new EndpointCatalog([typeof(ProbeEndpoint), typeof(ProbeEndpoint)], alreadyDiscovered: true);

        await Assert.That(catalog.Count).IsEqualTo(1);
        await Assert.That(catalog.Types.Single()).IsEqualTo(typeof(ProbeEndpoint));
    }

    [Test]
    public async Task DefinitionRejectsInvalidRoutesAndMissingMethod()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => new EndpointDefinition().Get("widgets")));
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => new EndpointDefinition().Get(" ")));
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => new EndpointDefinition().Method(" ", "/widgets")));

        var app = WebApplication.CreateBuilder().Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => new InvalidEndpoint().Map(app)));
        await app.DisposeAsync();
    }

    [Test]
    public async Task MappingUsesRouteAndOpenApiMetadata()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new EndpointCatalog([typeof(ProbeEndpoint)], alreadyDiscovered: true));
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        await app.StartAsync();

        var endpoint = app.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).Single();
        await Assert.That(endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.RouteNameMetadata>()?.RouteName)
            .IsEqualTo("probe");
        await Assert.That(endpoint.DisplayName).Contains("/probe");
        await app.StopAsync();
        await app.DisposeAsync();
    }

    [Test]
    public async Task DiscoveryDoesNotIncludeInterfacesOrAbstractEndpoints()
    {
        var types = EndpointDiscovery.Discover(typeof(EndpointTests).Assembly);

        await Assert.That(types).DoesNotContain(typeof(IEndpoint));
        await Assert.That(types).DoesNotContain(typeof(AbstractEndpoint));
    }
}

public sealed class ProbeEndpoint : EndpointWithoutRequest<string>
{
    public override void Configure(EndpointDefinition definition) => definition
        .Get("/probe")
        .WithName("probe")
        .WithSummary("Probe")
        .WithDescription("Probe description")
        .WithTags("Tests");

    public override Task<string> ExecuteAsync(CancellationToken cancellationToken) => Task.FromResult("ok");
}

public abstract class AbstractEndpoint : EndpointWithoutRequest<string>
{
}

public sealed class InvalidEndpoint : EndpointWithoutRequest<string>
{
    public override void Configure(EndpointDefinition definition) { }

    public override Task<string> ExecuteAsync(CancellationToken cancellationToken) => Task.FromResult("x");
}
