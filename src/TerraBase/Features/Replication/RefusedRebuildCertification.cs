using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>Keeps recovery refused until source-pinned, key-aware refill is integrated (A6 PR2).</summary>
internal sealed class RefusedRebuildCertification : IRebuildCertification
{
    public ValueTask<bool> RefillAsync(TenantContext database, Ulid newIncarnation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }

    public ValueTask<bool> CompleteAsync(TenantContext database, Ulid oldIncarnation, Ulid newIncarnation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}
