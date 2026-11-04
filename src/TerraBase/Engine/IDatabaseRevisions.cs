using TerraBase.Engine.Revisions;
using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal interface IDatabaseRevisions
{
    /// <summary>Raised after a commit gives a client database a newer revision. Handlers must not block.</summary>
    event Action<TenantContext>? Revised;

    ValueTask<RevisionPullResult?> PullAsync(TenantContext database, string? clientRevision, Action<uint, ReadOnlySpan<byte>> page, CancellationToken cancellationToken);

    ValueTask<bool> StartIncarnationAsync(TenantContext database, CancellationToken cancellationToken);
}
