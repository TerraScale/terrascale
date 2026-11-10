using TerraBase.Engine;
using TerraBase.Tenancy;

namespace TerraBase.Features.HomeApply;

internal interface IHomeApply
{
    ValueTask<HomeApplyResult?> ApplyAsync(TenantContext database, RegionTransaction transaction, CancellationToken cancellationToken);

    ValueTask<HomeApplyResult?> StateAsync(TenantContext database, string transactionId, CancellationToken cancellationToken);

    ValueTask PrepareAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask StampHomeWritesAsync(IStorageTransaction transaction, CancellationToken cancellationToken);

    ValueTask<bool> ClaimAsync(IStorageTransaction transaction, string id, CancellationToken cancellationToken);
}
