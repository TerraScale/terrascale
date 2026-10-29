using TerraBase.Engine.Revisions;
using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal interface IDatabaseRevisions
{
    ValueTask<RevisionPullResult?> PullAsync(TenantContext database, string? clientRevision, Action<uint, ReadOnlySpan<byte>> page, CancellationToken cancellationToken);

    ValueTask<bool> StartIncarnationAsync(TenantContext database, CancellationToken cancellationToken);
}
