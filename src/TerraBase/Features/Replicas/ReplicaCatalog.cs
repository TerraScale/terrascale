using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replicas;

/// <summary>
/// Every catalog read and write region replicas make: placements, the databases a region homes, and the records a replica
/// region keeps. One seam, so the SQL catalog (PIVOT E2) replaces the record catalog behind it without touching callers.
/// </summary>
internal interface IReplicaCatalog
{
    /// <summary>A database's name and placement as this region's catalog records it.</summary>
    ValueTask<DatabaseRecordResult> FindAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>One page of the databases recorded with home <paramref name="homeRegion"/>, after <paramref name="after"/>.</summary>
    ValueTask<ReadDatabaseRecordsResult> ListHomedAsync(string homeRegion, TenantContext? after, CancellationToken cancellationToken);

    /// <summary>Records a database another region homes and places here, enrolling its organization and project.</summary>
    ValueTask<bool> RecordAsync(ReplicaCatalogEntry entry, CancellationToken cancellationToken);

    /// <summary>Takes a newer placement its home decided.</summary>
    ValueTask UpdatePlacementAsync(TenantContext database, DatabasePlacement placement, CancellationToken cancellationToken);

    /// <summary>Forgets a database its home deleted: its record and local copy, or why the catalog could not.</summary>
    ValueTask<ForgetDatabaseResult> ForgetAsync(TenantContext database, CancellationToken cancellationToken);
}

/// <summary>The replica catalog over the management catalog (<see cref="ITenantDirectory"/>).</summary>
internal sealed class TenantReplicaCatalog(ITenantDirectory tenants, IProjectCatalog projects) : IReplicaCatalog
{
    internal const string Actor = "terrabase-replica";

    public ValueTask<DatabaseRecordResult> FindAsync(TenantContext database, CancellationToken cancellationToken) =>
        tenants.DatabaseRecordAsync(database, cancellationToken);

    public ValueTask<ReadDatabaseRecordsResult> ListHomedAsync(string homeRegion, TenantContext? after, CancellationToken cancellationToken) =>
        tenants.ReadDatabaseRecordsAsync(homeRegion, after, cancellationToken);

    public async ValueTask<bool> RecordAsync(ReplicaCatalogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!TerraBaseOptions.IsRegion(entry.HomeRegion)
            || await projects.ResolveProjectAsync(entry.ProjectId, Actor, entry.OrganizationId, cancellationToken).ConfigureAwait(false) is not string)
        {
            return false;
        }

        var database = new TenantContext(entry.OrganizationId, PlanTier.Hobby, entry.ProjectId, entry.DatabaseId);
        var placement = new DatabasePlacement(entry.HomeRegion, entry.ReplicaRegions, entry.PlacementVersion, entry.AllRegions);
        return await tenants.DatabaseRecordAsync(database, cancellationToken).ConfigureAwait(false) switch
        {
            NotFound => await tenants.AdoptDatabaseAsync(database, entry.Name, placement, cancellationToken).ConfigureAwait(false) is Done,
            TenantDatabaseRecord record when record.Placement.Version < placement.Version =>
                await tenants.UpdatePlacementAsync(database, placement, cancellationToken).ConfigureAwait(false) is PlacementUpdate,
            TenantDatabaseRecord => true,
            Unavailable => false,
        };
    }

    public async ValueTask UpdatePlacementAsync(TenantContext database, DatabasePlacement placement, CancellationToken cancellationToken) =>
        _ = await tenants.UpdatePlacementAsync(database, placement, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ForgetDatabaseResult> ForgetAsync(TenantContext database, CancellationToken cancellationToken) =>
        await tenants.ForgetDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
}
