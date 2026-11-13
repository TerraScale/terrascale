using TerraBase.Engine;
using TerraBase.Features.Sync;
using TerraBase.Tenancy;

namespace TerraBase.Features.HomeApply;

internal interface IHomeApply
{
    ValueTask<HomeApplyResult?> ApplyAsync(TenantContext database, RegionTransaction transaction, StreamSequence? position, CancellationToken cancellationToken);

    ValueTask<HomeApplyResult?> StateAsync(TenantContext database, string transactionId, CancellationToken cancellationToken);

    ValueTask PrepareAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<bool> StampHomeWritesAsync(IStorageTransaction transaction, CancellationToken cancellationToken);

    ValueTask<bool> ClaimAsync(IStorageTransaction transaction, string id, CancellationToken cancellationToken);
}
