using TerraBase.Errors;

namespace TerraBase.Tenancy;

public sealed record ResolvedProjectDatabase(TenantContext Database, DatabasePlacement Placement);

public readonly union ResolveProjectDatabaseResult(ResolvedProjectDatabase, NotFound, Unavailable);

public readonly union EnsureLocalDatabaseResult(Done, NotFound, Unavailable);

public readonly union FindDatabaseResult(TenantContext, NotFound, Unavailable);

public sealed record ProvisionedDatabase(TenantContext Database, bool Created);

public readonly union ProvisionDatabaseResult(ProvisionedDatabase, NotFound, Conflict, Unavailable);

public readonly union RegisteredDatabaseResult(bool, Unavailable);

public readonly union ReplicatesDatabaseResult(bool, Unavailable);

public readonly union DeletedDatabaseResult(bool, Unavailable);

public sealed record DatabaseIdentity(DateTimeOffset CreatedAt, string? BranchedFrom = null, DateTimeOffset? ExpiresAt = null)
{
    public static DatabaseIdentity? Reconcile(DatabaseIdentity? incoming, DatabaseIdentity? stored = null) =>
        incoming is null || incoming.CreatedAt == default ? stored
            : stored is not null && stored.CreatedAt < incoming.CreatedAt
                ? stored with { ExpiresAt = incoming.ExpiresAt, BranchedFrom = stored.BranchedFrom ?? incoming.BranchedFrom } : incoming;
}

public sealed record TenantDatabaseRecord(string Name, DatabasePlacement Placement, DatabaseIdentity Identity);

public readonly union DatabaseRecordResult(TenantDatabaseRecord, NotFound, Unavailable);

public readonly union ReadDatabaseRecordsResult(TenantDatabasePage, Unavailable);

public readonly union AdoptDatabaseResult(Done, NotFound, Conflict, Unavailable);

public sealed record PlacementUpdate(bool Updated);

public readonly union UpdatePlacementResult(PlacementUpdate, Unavailable);

public sealed record DatabaseForgotten(bool Forgotten);

public readonly union ForgetDatabaseResult(DatabaseForgotten, Unavailable);

public readonly union TenantPlanResult(PlanTier, Unavailable);

public readonly union TenantCatalogResult(Done, Unavailable);

public interface ITenantDirectory
{
    ValueTask<TenantCatalogResult> CheckCatalogAsync(CancellationToken cancellationToken);

    ValueTask<ResolveProjectDatabaseResult> ResolveProjectDatabaseAsync(string projectId, string database, CancellationToken cancellationToken);

    ValueTask<EnsureLocalDatabaseResult> EnsureLocalDatabaseAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<FindDatabaseResult> FindDatabaseAsync(string organizationId, string projectId, string database, CancellationToken cancellationToken);

    ValueTask<ProvisionDatabaseResult> CreateDatabaseAsync(
        string organizationId, string projectId, string database, CancellationToken cancellationToken);

    ValueTask<RegisteredDatabaseResult> RegisteredAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<ReplicatesDatabaseResult> ReplicatesAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<DeletedDatabaseResult> DeletedAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<DatabaseRecordResult> DatabaseRecordAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<ReadDatabaseRecordsResult> ReadDatabaseRecordsAsync(string homeRegion, TenantContext? after, CancellationToken cancellationToken);

    ValueTask<AdoptDatabaseResult> AdoptDatabaseAsync(TenantContext database, string name, DatabasePlacement placement, DatabaseIdentity? identity, CancellationToken cancellationToken);

    ValueTask<UpdatePlacementResult> UpdatePlacementAsync(
        TenantContext database, DatabasePlacement placement, CancellationToken cancellationToken, string? name = null, DatabaseIdentity? identity = null);

    ValueTask<bool> ReconcileDatabaseNameAsync(
        TenantContext database, string name, string home, long version, Func<TenantContext, CancellationToken, Task<string?>> resolve, CancellationToken cancellationToken);

    ValueTask<ForgetDatabaseResult> ForgetDatabaseAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<TenantPlanResult> PlanAsync(string organizationId, CancellationToken cancellationToken);
}

public sealed record DatabasePlacement(string Home, IReadOnlyList<string> Replicas, long Version, bool AllRegions = false)
{
    public bool PlacedIn(string region) => AllRegions || Home == region || Replicas.Contains(region);

    public IReadOnlyList<string> Regions => [.. Replicas.Append(Home).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
