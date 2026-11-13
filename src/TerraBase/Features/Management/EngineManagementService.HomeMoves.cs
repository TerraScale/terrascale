using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

public sealed record HomeMoveMarker(string MoveId, string OrganizationId, string ProjectId, string DatabaseId, string Target, long Version, IReadOnlyList<string>? RequestedRegions = null)
{
    public TenantContext Database => new(OrganizationId, PlanTier.Hobby, ProjectId, DatabaseId);
}

internal sealed record HomeMoveStart(HomeMoveMarker Marker, ManagedDatabase Database);

internal readonly union BeginHomeMoveResult(HomeMoveStart, ManagedDatabase, NotFound, Validation, Conflict, Unavailable);

internal readonly union CompleteHomeMoveResult(ManagedDatabase, NotFound, Unavailable);

internal interface IHomeMoveCatalog
{
    ValueTask<BeginHomeMoveResult> BeginHomeMoveAsync(TenantContext database, string target, IReadOnlyList<string>? requestedRegions, CancellationToken cancellationToken);

    ValueTask<CompleteHomeMoveResult> CompleteHomeMoveAsync(HomeMoveMarker marker, CancellationToken cancellationToken);

    ValueTask AbortHomeMoveAsync(HomeMoveMarker marker, CancellationToken cancellationToken);

    ValueTask<UpdatePlacementResult> FollowHomeLeaseAsync(TenantContext database, DatabasePlacement placement, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<HomeMoveMarker>> PendingHomeMovesAsync(CancellationToken cancellationToken);

    ValueTask<int> FollowServingRegionsAsync(IReadOnlyList<string> serving, CancellationToken cancellationToken);
}

internal sealed partial class EngineManagementService : IHomeMoveCatalog
{
    private const string HomeMoves = "home_moves";

    internal static readonly Conflict MoveInProgress = new("another move of this database's home is running", "move_in_progress");

    internal static async ValueTask<bool> HomeMovePendingAsync(
        IStorageTransaction transaction, string databaseId, CancellationToken cancellationToken) =>
        await GetAsync(transaction, HomeMoves, databaseId, ManagementJsonContext.Default.HomeMoveMarker, cancellationToken).ConfigureAwait(false) is not null;

    public ValueTask<BeginHomeMoveResult> BeginHomeMoveAsync(TenantContext database, string target, IReadOnlyList<string>? requestedRegions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return WriteAsync<BeginHomeMoveResult>(async transaction =>
        {
            if (database.ProjectId is null || database.DatabaseId is null
                || await CatalogTables.DatabaseAsync(transaction, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false) is not { } record)
            {
                return ManagementErrors.NotFound;
            }

            if (PlatformDatabases.IsPlatform(database.OrganizationId))
            {
                return ManagementErrors.PlatformPlacementLocked;
            }

            if (record.Home(options.Region) != options.Region)
            {
                return ManagementErrors.WrongRegion;
            }

            if (target == options.Region)
            {
                return record;
            }

            if (!record.AllRegions && !record.Replicas.Contains(target, StringComparer.Ordinal))
            {
                return new Validation(record.Replicas.Count == 0
                    ? "homeRegion must be one of the database's replica regions, and it has none: add the region as a replica first"
                    : $"homeRegion must be one of the database's replica regions: {string.Join(", ", record.Replicas)}");
            }

            if (await GetAsync(transaction, HomeMoves, database.DatabaseId, ManagementJsonContext.Default.HomeMoveMarker, cancellationToken).ConfigureAwait(false) is { } pending)
            {
                return DatabaseRegions.SameSet(pending.RequestedRegions, requestedRegions) ? RegionSetPlacement.PlacementNotReady : (BeginHomeMoveResult)MoveInProgress;
            }

            var marker = new HomeMoveMarker(EntityIds.New(time).ToString(), database.OrganizationId, database.ProjectId, database.DatabaseId, target, record.PlacementVersion + 1, requestedRegions);
            await PutAsync(transaction, HomeMoves, database.DatabaseId, marker, ManagementJsonContext.Default.HomeMoveMarker, cancellationToken).ConfigureAwait(false);
            return new HomeMoveStart(marker, record);
        }, static error => error, cancellationToken);
    }

    public ValueTask<CompleteHomeMoveResult> CompleteHomeMoveAsync(HomeMoveMarker marker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(marker);
        return WriteAsync<CompleteHomeMoveResult>(async transaction =>
        {
            _ = await transaction.DeleteAsync(HomeMoves, marker.DatabaseId, cancellationToken).ConfigureAwait(false);
            if (await CatalogTables.DatabaseAsync(transaction, marker.ProjectId, marker.DatabaseId, cancellationToken).ConfigureAwait(false) is not { } record)
            {
                return ManagementErrors.NotFound;
            }

            if (record.PlacementVersion >= marker.Version)
            {
                return record;
            }

            IReadOnlyList<string> replicas = [.. record.Replicas.Where(region => region != marker.Target).Append(options.Region).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            var moved = record with { HomeRegion = marker.Target, ReplicaRegions = replicas, PlacementVersion = marker.Version };
            await CatalogTables.PutDatabaseAsync(transaction, moved, cancellationToken).ConfigureAwait(false);
            _ = await transaction.DeleteAsync(EngineManagedBackupService.BackedUpDatabases, marker.DatabaseId, cancellationToken).ConfigureAwait(false);
            return moved;
        }, static error => error, cancellationToken);
    }

    public async ValueTask AbortHomeMoveAsync(HomeMoveMarker marker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(marker);
        _ = await WriteAsync<Done>(async transaction =>
        {
            if (await GetAsync(transaction, HomeMoves, marker.DatabaseId, ManagementJsonContext.Default.HomeMoveMarker, cancellationToken).ConfigureAwait(false) is { } current
                && current.MoveId == marker.MoveId)
            {
                _ = await transaction.DeleteAsync(HomeMoves, marker.DatabaseId, cancellationToken).ConfigureAwait(false);
            }

            return new Done();
        }, static _ => new Done(), cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<UpdatePlacementResult> FollowHomeLeaseAsync(TenantContext database, DatabasePlacement placement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(placement);
        return WriteAsync<UpdatePlacementResult>(async transaction =>
        {
            if (!Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false)))
            {
                return UnreadableCatalog;
            }

            if (database.ProjectId is null || database.DatabaseId is null
                || await CatalogTables.DatabaseAsync(transaction, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false) is not { } record
                || record.Home(options.Region) != options.Region || placement.Version <= record.PlacementVersion)
            {
                return new PlacementUpdate(Updated: false);
            }

            await CatalogTables.PutDatabaseAsync(transaction, record with
            {
                HomeRegion = placement.Home,
                ReplicaRegions = placement.Replicas.Count == 0 ? null : [.. placement.Replicas],
                PlacementVersion = placement.Version,
                AllRegions = placement.AllRegions,
            }, cancellationToken).ConfigureAwait(false);
            if (placement.Home != options.Region)
            {
                _ = await transaction.DeleteAsync(EngineManagedBackupService.BackedUpDatabases, database.DatabaseId, cancellationToken).ConfigureAwait(false);
            }

            return new PlacementUpdate(Updated: true);
        }, static error => error, cancellationToken);
    }

    public ValueTask<int> FollowServingRegionsAsync(IReadOnlyList<string> serving, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> replicas = DatabaseRegions.AllBut(serving, options.Region);
        return WriteAsync<int>(async transaction =>
        {
            var changed = 0;
            foreach (var (project, id) in await CatalogTables.AllRegionDatabasesAsync(transaction, options.Region, cancellationToken).ConfigureAwait(false))
            {
                if (await CatalogTables.DatabaseAsync(transaction, project, id, cancellationToken).ConfigureAwait(false) is { } record
                    && !record.Replicas.SequenceEqual(replicas, StringComparer.Ordinal)
                    && !await HomeMovePendingAsync(transaction, id, cancellationToken).ConfigureAwait(false))
                {
                    await CatalogTables.PutDatabaseAsync(transaction, record with
                    {
                        ReplicaRegions = replicas.Count == 0 ? null : [.. replicas],
                        PlacementVersion = record.PlacementVersion + 1,
                    }, cancellationToken).ConfigureAwait(false);
                    changed++;
                }
            }

            return changed;
        }, static _ => 0, cancellationToken);
    }

    public ValueTask<IReadOnlyList<HomeMoveMarker>> PendingHomeMovesAsync(CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<HomeMoveMarker>>(async transaction =>
        {
            var markers = new List<HomeMoveMarker>();
            string? after = null;
            do
            {
                var page = await transaction.ListAsync(HomeMoves, after, CatalogPage, MaxPageBytes, cancellationToken).ConfigureAwait(false);
                foreach (var record in page.Records)
                {
                    if (EntityPayload.Read(record, ManagementJsonContext.Default.HomeMoveMarker) is { } marker)
                    {
                        markers.Add(marker);
                    }
                }

                after = page.HasMore && page.Records.Count > 0 ? page.Records[^1].Id : null;
            }
            while (after is not null);
            return markers;
        }, static error => throw new InvalidOperationException(error.Message), cancellationToken);
}
