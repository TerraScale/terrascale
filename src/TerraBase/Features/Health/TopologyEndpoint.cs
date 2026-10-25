using TerraScale.Endpoints;

namespace TerraBase.Features.Health;

/// <summary>Peer-authenticated, internal topology comparison and loaded runtime peers.</summary>
public sealed class TopologyEndpoint(IPeerTopology topology) : EndpointWithoutRequest<TopologyStatus>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/v1/health/topology");
        Name("peerTopology");
        Summary("Admitted topology and runtime membership agreement");
        Tags("Administration");
        AllowAnonymous();
        Metadata(TerraBase.Hosting.ListenerMetadata.Internal);
    }

    /// <inheritdoc />
    public override async ValueTask<TopologyStatus> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return await topology.ReadAsync(cancellationToken).ConfigureAwait(false);
    }
}
