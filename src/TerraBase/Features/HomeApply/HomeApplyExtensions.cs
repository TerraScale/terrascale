using TerraBase.Features.Management;
using TerraBase.Tenancy;

namespace TerraBase.Features.HomeApply;

public static class HomeApplyExtensions
{
    public static IServiceCollection AddTerraBaseHomeApply(this IServiceCollection services) =>
        services.AddSingleton<IHomeApply, HomeApplier>();

    internal static async ValueTask<CreateDatabaseResult> PrepareReplicatedAsync(
        this IHomeApply apply, string organization, string region, CreateDatabaseResult result, CancellationToken cancellationToken)
    {
        if (result is ManagedDatabase database)
        {
            await PrepareAsync(apply, organization, region, database, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    internal static async ValueTask<SetDatabaseRegionsResult> PrepareReplicatedAsync(
        this IHomeApply apply, string organization, string region, SetDatabaseRegionsResult result, CancellationToken cancellationToken)
    {
        if (result is ManagedDatabase database)
        {
            await PrepareAsync(apply, organization, region, database, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static ValueTask PrepareAsync(IHomeApply apply, string organization, string region, ManagedDatabase database, CancellationToken cancellationToken) =>
        database is { ReplicaRegions.Count: > 0 } && (database.HomeRegion ?? region) == region
            ? apply.PrepareAsync(new TenantContext(organization, PlanTier.Hobby, database.ProjectId, database.Id), cancellationToken)
            : ValueTask.CompletedTask;
}
