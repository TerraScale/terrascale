using TerraBase.Engine.Revisions;
using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal interface IDatabaseRevisions
{
    /// <summary>Raised after a commit gives a client database a newer revision. Handlers must not block.</summary>
    event Action<TenantContext>? Revised;

    ValueTask<RevisionPullResult?> PullAsync(TenantContext database, string? clientRevision, IRevisionPullSink sink, CancellationToken cancellationToken);

    ValueTask<RevisionChecksums?> ChecksumAsync(TenantContext database, DatabaseRevision revision, ChecksumCursor? cursor, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReplicaPullObservation>> ReplicaStatusAsync(TenantContext database, IReadOnlyList<string> regions, CancellationToken cancellationToken);

    ValueTask RecordReplicaPullAsync(TenantContext database, string region, DatabaseRevision revision, CancellationToken cancellationToken);

    ValueTask<bool> StartIncarnationAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<bool> DrainWritersAsync(TenantContext database, CancellationToken cancellationToken);
}

internal interface IRevisionPullSink
{
    ValueTask StartAsync(RevisionPullStart start, CancellationToken cancellationToken);

    ValueTask WriteAsync(RevisionPageBatch batch, CancellationToken cancellationToken);
}

internal sealed record ReplicaPullObservation(string Region, double? LagMs, DateTimeOffset? LastPulledAt, string? Revision);
