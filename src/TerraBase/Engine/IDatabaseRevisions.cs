using TerraBase.Engine.Revisions;
using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal interface IDatabaseRevisions
{
    ValueTask<RevisionPullResult?> PullAsync(TenantContext database, string? clientRevision, IRevisionPullSink sink, CancellationToken cancellationToken);

    ValueTask<bool> StartIncarnationAsync(TenantContext database, CancellationToken cancellationToken);
}

internal interface IRevisionPullSink
{
    ValueTask StartAsync(RevisionPullStart start, CancellationToken cancellationToken);

    ValueTask WriteAsync(RevisionPageBatch batch, CancellationToken cancellationToken);
}
