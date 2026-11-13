using TerraBase.Engine;
using TerraBase.Tenancy;

namespace TerraBase.Features.HomeApply;

internal interface IHomeApply
{
    ValueTask<HomeApplyResult?> ApplyAsync(TenantContext database, RegionTransaction transaction, CancellationToken cancellationToken);

    ValueTask<HomeApplyResult?> StateAsync(TenantContext database, string transactionId, CancellationToken cancellationToken);

    ValueTask<HomeApplyResult?> ApplyPromotingAsync(TenantContext database, RegionTransaction transaction, CancellationToken cancellationToken);

    ValueTask PrepareAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask PreparePromotingAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<bool> StampHomeWritesAsync(IStorageTransaction transaction, CancellationToken cancellationToken);

    ValueTask<bool> ClaimAsync(IStorageTransaction transaction, string id, CancellationToken cancellationToken);
}
