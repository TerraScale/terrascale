using TerraBase.Engine.Revisions;
using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal interface IDatabaseRevisions
{
    /// <summary>Raised after a commit gives a client database a newer revision. Handlers must not block.</summary>
    event Action<TenantContext>? Revised;

    ValueTask<RevisionPullResult?> PullAsync(TenantContext database, string? clientRevision, IRevisionPullSink sink, CancellationToken cancellationToken);

    ValueTask<bool> StartIncarnationAsync(TenantContext database, CancellationToken cancellationToken);
}

internal interface IRevisionPullSink
{
    ValueTask StartAsync(RevisionPullStart start, CancellationToken cancellationToken);

    ValueTask WriteAsync(RevisionPageBatch batch, CancellationToken cancellationToken);
}
