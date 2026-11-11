using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Platform;
using TerraBase.Features.Qos;
using TerraBase.Features.Regions;
using TerraBase.Features.Catalog;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{

    public async ValueTask<CreateDatabaseResult> CreateDatabaseAsync(
        string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken, IReadOnlyList<string>? replicaRegions = null,
        bool allRegions = false, long? ttlSeconds = null) =>
        await AddDatabaseAsync(
            organizationId, transaction => AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken), name, sourceId: null, branchedFrom: null, cancellationToken,
            ttlSeconds: ttlSeconds,
            placement: allRegions ? new DatabasePlacement(options.Region, replicaRegions ?? [], 1, AllRegions: true)
                : replicaRegions is null ? null : new DatabasePlacement(options.Region, replicaRegions, 1))
            .ConfigureAwait(false) switch
        {
            AddedDatabase added => added.Database,
            NotFound error => error,
            Forbidden error => error,
            Conflict error => error,
            QuotaExceeded => throw new UnreachableException(),
            Unavailable error => error,
        };

    public async ValueTask<SetDatabaseRegionsResult> SetDatabaseRegionSetAsync(
        string userId, string organizationId, string projectId, string databaseId, IReadOnlyList<string> selected, CancellationToken cancellationToken)
    {
        var result = await WriteAsync<SetDatabaseRegionsResult>(async transaction =>
        {
            var project = await AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (project is not TenantContext)
            {
                return project switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    TenantContext => throw new UnreachableException(),
                };
            }

            if (await GetDatabaseAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is not { } database)
            {
                return ManagementErrors.NotFound;
            }

            if (database.Home(options.Region) != options.Region)
            {
                return ManagementErrors.WrongRegion;
            }

            if (await HomeMovePendingAsync(transaction, database.Id, cancellationToken).ConfigureAwait(false))
            {
                return MoveInProgress;
            }

            var replicas = selected.Contains(options.Region, StringComparer.Ordinal)
                ? DatabaseRegions.AllBut(selected, options.Region)
                : DatabaseRegions.AllBut(database.Replicas.Concat(selected), options.Region);
            if (DatabaseRegions.Check(replicas, options.Region) is Validation invalid)
            {
                return invalid;
            }

            if (!database.AllRegions && database.Replicas.Order(StringComparer.Ordinal).SequenceEqual(replicas))
            {
                return database;
            }

            return await SetDatabaseRegionsAsync(transaction, userId, organizationId, projectId, databaseId, replicas, cancellationToken).ConfigureAwait(false);
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (result is ManagedDatabase placed && engine is IDatabaseRevisions revisions)
        {
            var tenant = new TenantContext(organizationId, PlanTier.Hobby, projectId, databaseId);
            await revisions.RetainReplicaCdcAsync(tenant, placed.Placement(options.Region), null, null, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async ValueTask<SetDatabaseRegionsResult> SetDatabaseRegionsAsync(
        string userId, string organizationId, string projectId, string databaseId, IReadOnlyList<string> replicaRegions, CancellationToken cancellationToken,
        bool allRegions = false)
    {
        var result = await WriteAsync(
            transaction => SetDatabaseRegionsAsync(transaction, userId, organizationId, projectId, databaseId, replicaRegions, cancellationToken, allRegions), static error => error,
            cancellationToken).ConfigureAwait(false);
        if (result is ManagedDatabase database && engine is IDatabaseRevisions revisions)
        {
            var tenant = new TenantContext(organizationId, PlanTier.Hobby, projectId, databaseId);
            var placement = new DatabasePlacement(database.Home(options.Region), database.ReplicaRegions ?? [], database.PlacementVersion, database.AllRegions);
            await revisions.RetainReplicaCdcAsync(tenant, placement, null, null, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async ValueTask<SetDatabaseRegionsResult> SetDatabaseRegionsAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string databaseId, IReadOnlyList<string> replicaRegions,
        CancellationToken cancellationToken, bool allRegions = false)
    {
        var project = await AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
        if (project is not TenantContext tenant)
        {
            return project switch
            {
                NotFound error => error,
                Forbidden error => error,
                TenantContext => throw new UnreachableException(),
            };
        }

        if (await CatalogTables.DatabaseAsync(transaction, projectId, databaseId, cancellationToken).ConfigureAwait(false) is not { } database)
        {
            return ManagementErrors.NotFound;
        }

        if (PlatformDatabases.IsPlatform(tenant.OrganizationId))
        {
            return database;
        }

        if (database.Home(options.Region) != options.Region)
        {
            return ManagementErrors.WrongRegion;
        }

        if (await HomeMovePendingAsync(transaction, database.Id, cancellationToken).ConfigureAwait(false))
        {
            return MoveInProgress;
        }

        var placed = database with
        {
            HomeRegion = options.Region,
            ReplicaRegions = replicaRegions.Count == 0 ? null : [.. replicaRegions],
            PlacementVersion = database.PlacementVersion + 1,
            AllRegions = allRegions,
        };
        await CatalogTables.PutDatabaseAsync(transaction, placed, cancellationToken).ConfigureAwait(false);

        return placed;
    }

    public ValueTask<ListDatabasesResult> ListDatabasesAsync(
        string userId, string organizationId, string projectId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ListDatabasesResult>(async transaction =>
        {
            if (await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null)
            {
                return ManagementErrors.NotFound;
            }

            if (ProjectAccess.Current is { DatabaseName: not null } access)
            {
                var restricted = access.DatabaseId is null ? null
                    : await CatalogTables.DatabaseAsync(transaction, projectId, access.DatabaseId, cancellationToken).ConfigureAwait(false);
                return new ManagementPage<ManagedDatabase>(restricted is not null && (after is null || string.CompareOrdinal(restricted.Id, after) > 0)
                    ? [restricted] : [], null);
            }

            return await CatalogTables.DatabasesAsync(transaction, projectId, after, limit, cancellationToken).ConfigureAwait(false);
        }, static error => error, cancellationToken);

    public ValueTask<GetDatabaseResult> GetDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        ReadAsync<GetDatabaseResult>(async transaction =>
            await GetDatabaseAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is { } database
                ? database
                : ManagementErrors.NotFound,
            static error => error, cancellationToken);

    public ValueTask<ListProjectDatabasesResult> ListProjectDatabasesAsync(
        string projectId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ListProjectDatabasesResult>(
            async transaction => await CatalogTables.DatabasesAsync(transaction, projectId, after, limit, cancellationToken).ConfigureAwait(false),
            static error => error, cancellationToken);

    public ValueTask<DatabaseTenantResult> GetDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        ReadAsync<DatabaseTenantResult>(async transaction =>
            await AuthorizedDatabaseTenantAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is { } tenant
                ? tenant
                : ManagementErrors.NotFound,
            static error => error, cancellationToken);

    public ValueTask<WritableDatabaseTenantResult> GetWritableDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        ReadAsync<WritableDatabaseTenantResult>(async transaction =>
        {
            var access = await WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (access is not Organization)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    Organization => throw new UnreachableException(),
                };
            }

            return await AuthorizedDatabaseTenantAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is { } tenant
                ? tenant
                : ManagementErrors.NotFound;
        }, static error => error, cancellationToken);

    public ValueTask<WritableProjectResult> GetWritableProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        ReadAsync<WritableProjectResult>(async transaction =>
        {
            var access = await WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (access is not Organization)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    Organization => throw new UnreachableException(),
                };
            }

            return await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is { } project
                ? project
                : ManagementErrors.NotFound;
        }, static error => error, cancellationToken);

    internal static ValueTask<AuthorizedOrganization> WritableAsync(
        IStorageTransaction transaction, string userId, string organizationId, CancellationToken cancellationToken) =>
        AuthorizeAsync(transaction, userId, organizationId, allowProject: true, cancellationToken);


    public async ValueTask<BranchDatabaseResult> BranchDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, string name, CancellationToken cancellationToken, long? ttlSeconds = null)
    {
        var found = await GetDatabaseAsync(userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false);
        if (found is not ManagedDatabase source)
        {
            return found switch
            {
                NotFound error => error,
                Unavailable error => error,
                ManagedDatabase => throw new UnreachableException(),
            };
        }

        if (source.Home(options.Region) != options.Region)
        {
            return ManagementErrors.WrongRegion;
        }

        return await AddDatabaseAsync(
            organizationId, transaction => AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken), name, source.Id, source.Id, cancellationToken,
            ttlSeconds: ttlSeconds,
            placement: source.Placement(options.Region) with { Version = 1 })
            .ConfigureAwait(false) switch
        {
            AddedDatabase added => added.Database,
            NotFound error => error,
            Forbidden error => error,
            Conflict error => error,
            QuotaExceeded error => error,
            Unavailable error => error,
        };
    }

    public async ValueTask<CopyDatabaseResult> CopyDatabaseAsync(
        string userId, string organizationId, string projectId, string sourceId, string name, string? branchedFrom, CancellationToken cancellationToken, long? ttlSeconds = null)
    {
        DatabasePlacement? placement = null;
        if (branchedFrom is not null)
        {
            var found = await GetDatabaseAsync(userId, organizationId, projectId, branchedFrom, cancellationToken).ConfigureAwait(false);
            if (found is not ManagedDatabase source)
            {
                return found switch
                {
                    NotFound error => error,
                    Unavailable error => error,
                    ManagedDatabase => throw new UnreachableException(),
                };
            }

            if (!source.PlacedIn(options.Region))
            {
                return new Conflict("the snapshot's region is outside the database's current placement", "backup_region_not_placed");
            }

            placement = source.Placement(options.Region) with { Version = 1 };
        }

        return await AddDatabaseAsync(
            organizationId, transaction => AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken), name, sourceId, branchedFrom, cancellationToken, placement: placement, ttlSeconds: ttlSeconds)
            .ConfigureAwait(false) switch
        {
            AddedDatabase added => added.Database,
            NotFound error => error,
            Forbidden error => error,
            Conflict error => error,
            QuotaExceeded error => error,
            Unavailable error => error,
        };
    }

    public async ValueTask<RestorePrecheckResult> PrecheckRestoreAsync(
        string userId, string organizationId, string projectId, string? name, CancellationToken cancellationToken)
    {
        var precheck = await PrecheckAsync(
            transaction => AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken),
            name, await AllowanceAsync(organizationId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return precheck switch
        {
            TenantContext => new Done(),
            NotFound error => error,
            Forbidden error => error,
            Conflict error => error,
            Unavailable error => error,
        };
    }
}
