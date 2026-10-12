using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Health;

/// <summary>
/// <c>GET /health/replication</c>: this node's region, the other regions it sees and its replication lag towards each, for
/// status pages. Unauthenticated, on the administrative and client listeners; no IDs, tenant data or secrets.
/// </summary>
public sealed class ReplicationHealthEndpoint(IReplicationHealth health) : EndpointWithoutRequest<ReplicationHealthBody>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/health/replication");
        Name("replicationHealth");
        Summary("Replication health of this node");
        Description("Region codes and lag seconds only. status is degraded when a region this node sees has no replication stream, lags more than 30 seconds or diverged. Cacheable for 5 seconds. Pick the answering region with Fly's fly-prefer-region request header.");
        Tags("Health");
        AllowAnonymous();
        Metadata(ListenerMetadata.Probe);
    }

    /// <inheritdoc />
    public override async ValueTask<ReplicationHealthBody> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "public, max-age=5";
        return await health.ReadAsync(cancellationToken).ConfigureAwait(false);
    }
}
