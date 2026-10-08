using TerraBase.Configuration;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replication;

/// <summary>Loopback administrative outbound replication status.</summary>
public sealed class ReplicationStatusEndpoint(IReplicationStatus status, TerraBaseOptions options) : EndpointWithoutRequest<ReplicationStatus>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/replication");
        Name("replicationStatus");
        Summary("Local outbound replication status");
        Description("Loopback only. Validated durable prefixes, detected journal divergence and catch-up times. Detail is bounded to 128 database incarnations; omitted databases remain in total lag. This is not an inbound or document-equality check.");
        Tags("Administration");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<ReplicationStatus> HandleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HttpContext.Response.Headers.CacheControl = "no-store";
        var snapshot = status.Snapshot();
        return ValueTask.FromResult(snapshot with { Enabled = options.PeerPort is not null });
    }
}
