using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replicas;

internal interface IReplicaCatalog
{
    ValueTask<DatabaseRecordResult> FindAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<ReadDatabaseRecordsResult> ListHomedAsync(string homeRegion, TenantContext? after, CancellationToken cancellationToken);

    ValueTask<bool> RecordAsync(ReplicaCatalogEntry entry, CancellationToken cancellationToken,
        IReadOnlyDictionary<(string, string, string), ReplicaCatalogEntry>? listed = null, ReplicaNameLookups? lookups = null);

    ValueTask UpdatePlacementAsync(TenantContext database, DatabasePlacement placement, CancellationToken cancellationToken);

    ValueTask<ForgetDatabaseResult> ForgetAsync(TenantContext database, CancellationToken cancellationToken);
}

internal sealed class TenantReplicaCatalog(ITenantDirectory tenants, IProjectCatalog projects, ReplicaNames names) : IReplicaCatalog
{
    internal const string Actor = "terrabase-replica";

    public ValueTask<DatabaseRecordResult> FindAsync(TenantContext database, CancellationToken cancellationToken) =>
        tenants.DatabaseRecordAsync(database, cancellationToken);

    public ValueTask<ReadDatabaseRecordsResult> ListHomedAsync(string homeRegion, TenantContext? after, CancellationToken cancellationToken) =>
        tenants.ReadDatabaseRecordsAsync(homeRegion, after, cancellationToken);

    public async ValueTask<bool> RecordAsync(ReplicaCatalogEntry entry, CancellationToken cancellationToken,
        IReadOnlyDictionary<(string, string, string), ReplicaCatalogEntry>? listed = null, ReplicaNameLookups? lookups = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!TerraBaseOptions.IsRegion(entry.HomeRegion)
            || await projects.ResolveProjectAsync(entry.ProjectId, Actor, entry.OrganizationId, cancellationToken).ConfigureAwait(false) is not string)
        {
            return false;
        }

        var database = new TenantContext(entry.OrganizationId, PlanTier.Hobby, entry.ProjectId, entry.DatabaseId);
        var placement = new DatabasePlacement(entry.HomeRegion, entry.ReplicaRegions, entry.PlacementVersion, entry.AllRegions);
        var stored = await tenants.DatabaseRecordAsync(database, cancellationToken).ConfigureAwait(false);
        if (stored is TenantDatabaseRecord unchanged && unchanged.Name == entry.Name
            && unchanged.Placement.Home == placement.Home && unchanged.Placement.Version == placement.Version
            && unchanged.Placement.AllRegions == placement.AllRegions && unchanged.Placement.Replicas.SequenceEqual(placement.Replicas)
            && unchanged.Identity == DatabaseIdentity.Reconcile(entry.Identity, unchanged.Identity))
        {
            return true;
        }

        var nameApplied = false;
        if (stored is NotFound || stored is TenantDatabaseRecord existing && existing.Placement.Version <= placement.Version && existing.Placement.Home == entry.HomeRegion)
        {
            using var owned = lookups is null ? new ReplicaNameLookups(cancellationToken) : null;
            nameApplied = await names.ApplyAsync(entry, listed, lookups ?? owned!, cancellationToken).ConfigureAwait(false);
        }

        return stored switch
        {
            NotFound => await tenants.AdoptDatabaseAsync(database, entry.Name, placement, entry.Identity, cancellationToken).ConfigureAwait(false) is Done,
            TenantDatabaseRecord record when record.Placement.Version <= placement.Version =>
                await tenants.UpdatePlacementAsync(database, placement, cancellationToken, nameApplied ? entry.Name : null, entry.Identity).ConfigureAwait(false) is PlacementUpdate,
            TenantDatabaseRecord => true,
            Unavailable => false,
        };
    }

    public async ValueTask UpdatePlacementAsync(TenantContext database, DatabasePlacement placement, CancellationToken cancellationToken) =>
        _ = await tenants.UpdatePlacementAsync(database, placement, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ForgetDatabaseResult> ForgetAsync(TenantContext database, CancellationToken cancellationToken) =>
        await tenants.ForgetDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
}
