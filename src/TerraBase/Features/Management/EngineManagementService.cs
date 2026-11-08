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
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService(
    IStorageEngine engine, IOrphanDatabaseFiles orphanFiles, TimeProvider time, TerraBaseOptions options, ILogger<EngineManagementService> logger, DatabaseLifecycleCheckpoint checkpoint, IMeterFactory meters,
    IProjectAuthorityVerifier projectAuthority, Lazy<IStorageQuota> quota, IRegionPeers regionPeers,
    Lazy<ICatalogGate> catalogGate, DatabaseAllowance databaseAllowance, Lazy<IBackupKeyShredding> shredding, RemoteDatabases remote)
    : IManagementService, ITenantDirectory, IHostedService, ICatalogGuard, IDatabaseFileCatalog
{
    private const string Catalog = ManagementCatalog.Kind;
    private const string CatalogVersionId = ManagementCatalog.VersionId;

    private const string CatalogVersion = ManagementCatalog.Version;

    internal const string RetiredDatabases = "retired_databases";
    private const long MaxPageBytes = 1024 * 1024;

    private const int CatalogPage = 100;

    private static readonly JsonTypeInfo<string> Id = TerraBaseJsonContext.Default.String;

    private volatile bool _catalogCurrent;

    public ValueTask<CreateOrganizationResult> CreateOrganizationAsync(string userId, string name, CancellationToken cancellationToken) =>
        CreateOrganizationAsync(userId, NewId(), name, cancellationToken);

    private async ValueTask<CreateOrganizationResult> CreateOrganizationAsync(string userId, string id, string name, CancellationToken cancellationToken)
    {
        var organization = new Organization(id, name, userId, time.GetUtcNow());
        return await WriteAsync<CreateOrganizationResult>(async transaction =>
        {
            if (await CatalogTables.OrganizationAsync(transaction, id, cancellationToken).ConfigureAwait(false) is { } known)
            {
                return known.Name == name && known.CreatedBy == userId ? known : ManagementErrors.IdTaken;
            }

            if (await CatalogTables.RetiredAsync(transaction, id, cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.IdTaken;
            }

            if (await CatalogTables.OrganizationNamedAsync(transaction, userId, Key(name), cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementErrors.NameTaken;
            }

            _ = await CatalogTables.PutOrganizationAsync(transaction, organization, cancellationToken).ConfigureAwait(false);
            return organization;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ListOrganizationsResult> ListOrganizationsAsync(string userId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ListOrganizationsResult>(async transaction =>
            await CatalogTables.OrganizationsCreatedByAsync(transaction, userId, after, limit, cancellationToken).ConfigureAwait(false),
            static error => error, cancellationToken);

    public ValueTask<GetOrganizationResult> GetOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        ReadAsync<GetOrganizationResult>(async transaction =>
            await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is { } organization
                ? organization
                : ManagementErrors.NotFound,
            static error => error, cancellationToken);

    public async ValueTask<SetPlanResult> SetPlanAsync(
        string userId, string organizationId, PlanTier plan, CancellationToken cancellationToken) =>
        await WriteAsync<SetPlanResult>(async transaction =>
        {
            var access = await AuthorizeAsync(transaction, userId, organizationId, allowProject: false, cancellationToken).ConfigureAwait(false);
            if (access is not Organization)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    Organization => throw new UnreachableException(),
                };
            }

            var organization = await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("an organization index names a missing organization");
            organization = organization with { Plan = plan };
            _ = await CatalogTables.PutOrganizationAsync(transaction, organization, cancellationToken).ConfigureAwait(false);

            return organization;
        }, static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<DeleteOrganizationResult> DeleteOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        await WriteAsync<DeleteOrganizationResult>(async transaction =>
        {
            var access = await AuthorizeAsync(transaction, userId, organizationId, allowProject: false, cancellationToken).ConfigureAwait(false);
            if (access is not Organization)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    Organization => throw new UnreachableException(),
                };
            }

            var organization = await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("an organization index names a missing organization");

            if (await CatalogTables.HasProjectsAsync(transaction, organization.Id, cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.NotEmpty;
            }

            await CatalogTables.DeleteOrganizationAsync(transaction, organization.Id, cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);

    public ValueTask<CreateProjectResult> CreateProjectAsync(
        string userId, string organizationId, string name, CancellationToken cancellationToken) =>
        CreateProjectAsync(userId, organizationId, NewId(), name, cancellationToken);

    private async ValueTask<CreateProjectResult> CreateProjectAsync(
        string userId, string organizationId, string id, string name, CancellationToken cancellationToken)
    {
        var project = new Project(id, organizationId, name, time.GetUtcNow());
        return await WriteAsync<CreateProjectResult>(async transaction =>
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

            if (await CatalogTables.ProjectAsync(transaction, id, cancellationToken).ConfigureAwait(false) is { } known)
            {
                return known.OrganizationId == organizationId && known.Name == name ? known : ManagementErrors.IdTaken;
            }

            if (await CatalogTables.RetiredAsync(transaction, id, cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.IdTaken;
            }

            if (await CatalogTables.ProjectNamedAsync(transaction, organizationId, Key(name), cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementErrors.NameTaken;
            }

            _ = await CatalogTables.PutProjectAsync(transaction, project, cancellationToken).ConfigureAwait(false);
            return project;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ListProjectsResult> ListProjectsAsync(
        string userId, string organizationId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ListProjectsResult>(async transaction =>
            await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is null
                ? ManagementErrors.NotFound
                : await CatalogTables.ProjectsAsync(transaction, organizationId, after, limit, cancellationToken).ConfigureAwait(false),
            static error => error, cancellationToken);

    public ValueTask<GetProjectResult> GetProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        ReadAsync<GetProjectResult>(async transaction =>
            await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is { } project
                ? project
                : ManagementErrors.NotFound,
            static error => error, cancellationToken);

    public async ValueTask<DeleteProjectResult> DeleteProjectAsync(
        string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await WriteAsync<DeleteProjectResult>(async transaction =>
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

            if (await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is not { } project)
            {
                return ManagementErrors.NotFound;
            }

            if (await CatalogTables.HasDatabasesAsync(transaction, project.Id, cancellationToken).ConfigureAwait(false)
                || await AnyAsync(transaction, EngineApiKeyStore.ProjectKeys(project.Id), cancellationToken).ConfigureAwait(false)
                || await AnyAsync(transaction, ProjectBackupDestinations.Kind(project.Id), cancellationToken).ConfigureAwait(false)
                || await AnyAsync(transaction, Features.FileStorage.FileStorageUsage.Buckets(project.Id), cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.NotEmpty;
            }

            await CatalogTables.DeleteProjectAsync(transaction, project.Id, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteKindsAsync(SystemCatalog.ProjectState(project.Id), cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<CreateDatabaseResult> CreateDatabaseAsync(
        string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken, IReadOnlyList<string>? replicaRegions = null,
        bool allRegions = false, long? ttlSeconds = null) =>
        await AddDatabaseAsync(
            transaction => AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken), name, sourceId: null, branchedFrom: null, cancellationToken,
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

    public async ValueTask<DeleteDatabaseResult> DeleteDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken)
    {
        var target = await ReadAsync<DeletableDatabaseResult>(
            async transaction => await DeletableAsync(transaction, userId, organizationId, projectId, databaseId, options.Region, cancellationToken).ConfigureAwait(false) switch
            {
                DeletableDatabase deletable => deletable,
                NotFound error => error,
                Forbidden error => error,
                Conflict error => error,
            },
            static error => error, cancellationToken).ConfigureAwait(false);
        if (target is not DeletableDatabase deletable)
        {
            return target switch
            {
                NotFound error => error,
                Forbidden error => error,
                Conflict error => error,
                Unavailable error => error,
                DeletableDatabase => throw new UnreachableException(),
            };
        }

        // The fan-out must use the placement the commit acted on: a placement change committing in between would
        // otherwise leave the regions it added un-notified until their own sweeps.
        ManagedDatabase? committed = null;
        bool Unregistered(ManagedDatabase record)
        {
            committed = record;
            return true;
        }

        return await DeleteRecordedAsync(
            deletable.Storage,
            async (transaction, token) =>
                await UnregisterDatabaseAsync(transaction, userId, organizationId, projectId, databaseId, options.Region, token).ConfigureAwait(false) switch
                {
                    DeletableDatabase unregistered => Unregistered(unregistered.Record),
                    _ => false,
                },
            cancellationToken).ConfigureAwait(false) switch
        {
            bool deleted => !deleted ? ManagementErrors.NotFound
                : await ForgetReplicaRegionsAsync(deletable.Storage, committed ?? deletable.Record, CancellationToken.None).ConfigureAwait(false),
            Unavailable refused => refused,
        };
    }

    // The home tells the regions that hold the database's record directly, so their catalogs and every merged listing
    // drop it within seconds. Best effort and bounded: a region that misses the push forgets the database on its own
    // sweep, and the merged listing serves only what a reachable home still lists.
    private async ValueTask<DeleteDatabaseResult> ForgetReplicaRegionsAsync(TenantContext storage, ManagedDatabase record, CancellationToken cancellationToken)
    {
        try
        {
            var pushes = (await regionPeers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false))
                .Where(peer => record.AllRegions || record.Replicas.Contains(peer.Region, StringComparer.Ordinal))
                .Select(peer => remote.ForgetAsync(peer.Region, peer.Address, storage.OrganizationId, storage.ProjectId!, storage.DatabaseId!, cancellationToken))
                .ToArray();
            if (pushes.Length > 0)
            {
                await Task.WhenAll(pushes).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // The delete already committed: the push must not turn its answer into a 500 or skip key destruction.
            logger.ZLogWarning(error, $"Telling replica regions to forget deleted database {storage.DatabaseId:@Database} failed ({error.GetType().Name:@Error}); their own sweeps forget it");
        }

        return await shredding.Value.DestroyAsync(storage, cancellationToken).ConfigureAwait(false) ? new Done()
            : new Unavailable("database deletion committed; key destruction is pending and will retry");
    }

    private Done Shredded(TenantContext storage)
    {
        shredding.Value.Shred(storage);
        return new Done();
    }

    private async ValueTask<RecordedDeletion> DeleteRecordedAsync(
        TenantContext storage, Func<IStorageTransaction, CancellationToken, ValueTask<bool>> unregister, CancellationToken cancellationToken)
    {
        var recording = false;
        RecordedDeletion decision = false;
        bool recorded;
        try
        {
            recorded = await engine.RecordDatabaseDeleteIntentAsync(storage, async token =>
            {
                recording = true;
                checkpoint("delete.recording", storage);
                var result = await WriteAsync<RecordedDeletion>(async transaction => await unregister(transaction, token).ConfigureAwait(false),
                    static error => error, token).ConfigureAwait(false);
                decision = result;
                return result switch
                {
                    bool accepted => accepted,
                    Unavailable => false,
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (recording)
            {
                await ReleaseUnrecordedIntentAsync(storage).ConfigureAwait(false);
            }

            throw;
        }
        if (!recorded)
        {
            return decision;
        }

        checkpoint("delete.intent", storage);
        var cleanup = await FinishDatabaseCleanupAsync(storage, CancellationToken.None).ConfigureAwait(false);
        if (cleanup is Unavailable error)
        {
            return error;
        }

        checkpoint("delete.finalized", storage);
        return true;
    }

    private readonly union RecordedDeletion(bool, Unavailable);

    private readonly union DeletionPlacement(Done, Conflict);

    private static DeletionPlacement CheckDeletionPlacement(ManagedDatabase database, string region) =>
        database.Home(region) != region ? ManagementErrors.WrongRegion : new Done();

    private static async ValueTask<DeletableDatabaseCheck> DeletableAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string databaseId, string region, CancellationToken cancellationToken)
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

        return CheckDeletionPlacement(database, region) is Conflict refused ? refused : new DeletableDatabase(tenant with { DatabaseId = database.Id }, database);
    }

    // Re-reads the database inside the commit transaction, so the record it returns — and its placement — is the one
    // the delete acted on, not whatever an earlier read outside the transaction saw.
    private async ValueTask<UnregisterResult> UnregisterDatabaseAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string databaseId, string region, CancellationToken cancellationToken)
    {
        var deletable = await DeletableAsync(transaction, userId, organizationId, projectId, databaseId, region, cancellationToken).ConfigureAwait(false) switch
        {
            DeletableDatabase found => (UnregisterResult)found,
            NotFound error => error,
            Forbidden error => error,
            Conflict error => error,
        };
        if (deletable is DeletableDatabase record)
        {
            _ = await UnregisterRecordAsync(transaction, record.Storage, record.Record, cancellationToken, customerDeletion: true).ConfigureAwait(false);
            return record;
        }

        return deletable;
    }

    private async ValueTask<TenantContext> UnregisterRecordAsync(
        IStorageTransaction transaction, TenantContext storage, ManagedDatabase database, CancellationToken cancellationToken, bool customerDeletion = false)
    {
        if (customerDeletion)
        {
            await RecordDatabasePurgeAsync(transaction, storage, database, cancellationToken).ConfigureAwait(false);
            await BackupDestinationHistory.CaptureAsync(transaction, storage, cancellationToken).ConfigureAwait(false);
        }
        _ = await CatalogTables.PutPendingCleanupAsync(transaction, storage, cancellationToken).ConfigureAwait(false);
        _ = await CatalogTables.DeleteDatabaseAsync(transaction, database.Id, cancellationToken).ConfigureAwait(false);
        await StorageSweep.AddAsync(transaction, new SweepEntry(StorageSweep.DatabaseBackups, storage.OrganizationId, database.Id), cancellationToken).ConfigureAwait(false);
        _ = await transaction.DeleteAsync(EngineManagedBackupService.BackedUpDatabases, database.Id, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, RetiredDatabases, database.Id, DatabaseOwner.Of(storage), TerraBaseJsonContext.Default.DatabaseOwner, cancellationToken)
            .ConfigureAwait(false);

        await transaction.DeleteKindsAsync(SystemCatalog.DatabaseState(database.Id), cancellationToken).ConfigureAwait(false);

        return storage;
    }

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
            transaction => AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken), name, source.Id, source.Id, cancellationToken,
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
            transaction => AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken), name, sourceId, branchedFrom, cancellationToken, placement: placement, ttlSeconds: ttlSeconds)
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

    /// <inheritdoc />
    public async ValueTask<RestorePrecheckResult> PrecheckRestoreAsync(
        string userId, string organizationId, string projectId, string? name, CancellationToken cancellationToken)
    {
        // The same checks AddDatabaseAsync makes, before a restore spends its replay: the allowance every copy answers
        // and, for a caller-chosen name, that the name is free. An automatic name is chosen after the replay, so it is
        // checked there. The registration re-checks both when it commits.
        var precheck = await PrecheckAsync(
            transaction => AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken),
            name, new AllowanceCheck(databaseAllowance.Limit), cancellationToken).ConfigureAwait(false);
        return precheck switch
        {
            TenantContext => new Done(),
            NotFound error => error,
            Forbidden error => error,
            Conflict error => error,
            Unavailable error => error,
        };
    }

    private async ValueTask<AddDatabaseResult> AddDatabaseAsync(
        Func<IStorageTransaction, ValueTask<ProjectTenant>> project, string name, string? sourceId, string? branchedFrom, CancellationToken cancellationToken,
        string? id = null, DatabasePlacement? placement = null, DateTimeOffset? createdAt = null, long? ttlSeconds = null)
    {
        var allowance = sourceId is not null || (placement?.Home ?? options.Region) == options.Region
            ? new AllowanceCheck(databaseAllowance.Limit)
            : null;

        var precheck = await PrecheckAsync(project, name, allowance, cancellationToken).ConfigureAwait(false);

        if (precheck is not TenantContext tenant)
        {
            return Refused(precheck);
        }

        StorageQuotaGroupLease? lease = null;
        if (sourceId is not null)
        {
            var entered = await quota.Value.EnterOrganizationsAsync([tenant.OrganizationId], cancellationToken).ConfigureAwait(false);
            if (entered is not StorageQuotaGroupLease granted)
            {
                return entered switch
                {
                    Unavailable error => error,
                    StorageQuotaGroupLease => throw new UnreachableException(),
                };
            }

            lease = granted;
        }

        using var admission = lease;
        placement ??= new DatabasePlacement(options.Region, [], 1);

        var database = new ManagedDatabase(
            id ?? NewId(), tenant.ProjectId!, name, branchedFrom, createdAt ?? time.GetUtcNow(), placement.Home, placement.Replicas.Count == 0 ? null : [.. placement.Replicas], placement.Version,
            placement.AllRegions, ExpiresAt: DatabaseTtl.ExpiresAt(time.GetUtcNow(), ttlSeconds));
        var storage = tenant with { DatabaseId = database.Id };
        var preparation = await PrepareDatabaseAsync(storage, sourceId is null ? null : tenant with { DatabaseId = sourceId }, cancellationToken).ConfigureAwait(false);
        if (preparation is not bool created || !created)
        {
            return preparation switch
            {
                bool => ManagementErrors.NotFound,
                Unavailable error => error,
            };
        }

        AddDatabasePrecheck registered;
        try
        {
            if (admission is not null
                && admission.RequiresAccounting(tenant.OrganizationId)
                && await admission.CheckAsync(tenant.OrganizationId,
                    await quota.Value.ReadBytesAsync(storage, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false) is not Done and var exceeded)
            {
                await AbandonDatabaseAsync(storage).ConfigureAwait(false);
                return exceeded switch
                {
                    QuotaExceeded error => error,
                    Unavailable error => error,
                    Done => throw new UnreachableException(),
                };
            }

            registered = await RegisterAsync(project, database, allowance, cancellationToken).ConfigureAwait(false);

            if (registered is TenantContext)
            {
                checkpoint("create.committed", storage);
            }
        }
        catch (QueryCapacityException)
        {
            await AbandonDatabaseAsync(storage).ConfigureAwait(false);
            return StorageQuota.AccountingUnavailable;
        }
        catch
        {
            await AbandonDatabaseAsync(storage).ConfigureAwait(false);
            throw;
        }

        if (registered is Unavailable refused)
        {
            return refused;
        }

        if (registered is not TenantContext)
        {
            return await FinishDatabaseCleanupAsync(storage, CancellationToken.None).ConfigureAwait(false) switch
            {
                Done => Refused(registered),
                Unavailable error => error,
            };
        }

        await engine.CompleteDatabaseIntentAsync(storage, CancellationToken.None).ConfigureAwait(false);
        checkpoint("create.registered", storage);
        return new AddedDatabase(database, storage);
    }

    private ValueTask<AddDatabasePrecheck> PrecheckAsync(
        Func<IStorageTransaction, ValueTask<ProjectTenant>> project, string? name, AllowanceCheck? allowance, CancellationToken cancellationToken) =>
        ReadAsync<AddDatabasePrecheck>(
            async transaction => await CanAddDatabaseAsync(transaction, project, name, cancellationToken, allowance).ConfigureAwait(false) switch
            {
                TenantContext free => free,
                NotFound error => error,
                Forbidden error => error,
                Conflict error => error,
            },
            static error => error, cancellationToken);

    private ValueTask<AddDatabasePrecheck> RegisterAsync(
        Func<IStorageTransaction, ValueTask<ProjectTenant>> project, ManagedDatabase database, AllowanceCheck? allowance, CancellationToken cancellationToken) =>
        WriteAsync<AddDatabasePrecheck>(async transaction =>
        {
            var registration = await RegisterDatabaseAsync(transaction, project, database, allowance, cancellationToken)
                .ConfigureAwait(false);

            return registration switch
            {
                TenantContext free => free,
                NotFound error => error,
                Forbidden error => error,
                Conflict error => error,
            };
        }, static error => error, cancellationToken);

    private static AddDatabaseResult Refused(AddDatabasePrecheck check) => check switch
    {
        NotFound error => error,
        Forbidden error => error,
        Conflict error => error,
        Unavailable error => error,
        TenantContext => throw new UnreachableException(),
    };

    private async ValueTask<RecordedDeletion> PrepareDatabaseAsync(TenantContext storage, TenantContext? source, CancellationToken cancellationToken)
    {
        checkpoint("create.prepare", storage);
        var recording = false;
        var publishing = false;
        RecordedDeletion decision = false;
        bool created;
        try
        {
            created = await engine.CreateDatabaseWithIntentAsync(storage, source, async token =>
            {
                recording = true;
                decision = await WriteAsync<RecordedDeletion>(async transaction =>
                {
                    if (await DatabasePurge.IsDeletedAsync(transaction, storage.DatabaseId!, token).ConfigureAwait(false)
                        || source is not null && await DatabasePurge.IsDeletedAsync(transaction, source.DatabaseId!, token).ConfigureAwait(false))
                    {
                        return false;
                    }

                    _ = await CatalogTables.PutPendingCleanupAsync(transaction, storage, token).ConfigureAwait(false);
                    return true;
                }, static error => error, token).ConfigureAwait(false);
                if (decision is not bool accepted || !accepted)
                {
                    return false;
                }

                checkpoint("create.intent", storage);
                publishing = true;
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (publishing)
            {
                _ = await FinishDatabaseCleanupAsync(storage, CancellationToken.None).ConfigureAwait(false);
            }
            else if (recording)
            {
                await ReleaseUnrecordedIntentAsync(storage).ConfigureAwait(false);
            }

            throw;
        }

        if (!created)
        {
            return decision;
        }

        checkpoint("create.files", storage);
        return true;
    }

    private static async ValueTask<AddDatabaseCheck> RegisterDatabaseAsync(
        IStorageTransaction transaction, Func<IStorageTransaction, ValueTask<ProjectTenant>> project, ManagedDatabase database,
        AllowanceCheck? allowance, CancellationToken cancellationToken)
    {
        if (await DatabasePurge.IsDeletedAsync(transaction, database.Id, cancellationToken).ConfigureAwait(false))
        {
            return ManagementErrors.IdTaken;
        }

        var check = await CanAddDatabaseAsync(transaction, project, database.Name, cancellationToken, allowance).ConfigureAwait(false);
        if (check is TenantContext tenant)
        {
            await CatalogTables.PutDatabaseAsync(transaction, database, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, EngineManagedBackupService.BackedUpDatabases, database.Id, DatabaseOwner.Of(tenant with { DatabaseId = database.Id }),
                TerraBaseJsonContext.Default.DatabaseOwner, cancellationToken).ConfigureAwait(false);
            _ = await CatalogTables.DeletePendingCleanupAsync(transaction, database.Id, cancellationToken).ConfigureAwait(false);
        }

        return check;
    }

    private async ValueTask AbandonDatabaseAsync(TenantContext storage)
    {
        var registered = await ReadAsync<RecordedDeletion>(async transaction =>
            await CatalogTables.DatabaseAsync(transaction, storage.ProjectId!, storage.DatabaseId!, CancellationToken.None).ConfigureAwait(false) is not null
            && await CatalogTables.PendingCleanupAsync(transaction, storage.DatabaseId!, CancellationToken.None).ConfigureAwait(false) is null,
            static error => error, CancellationToken.None).ConfigureAwait(false);
        if (registered is not bool accepted)
        {
            return;
        }

        if (accepted)
        {
            await engine.CompleteDatabaseIntentAsync(storage, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            _ = await FinishDatabaseCleanupAsync(storage, CancellationToken.None, abandonedCreation: true).ConfigureAwait(false);
        }
    }

    private static async ValueTask<AddDatabaseCheck> CanAddDatabaseAsync(
        IStorageTransaction transaction, Func<IStorageTransaction, ValueTask<ProjectTenant>> project, string? name, CancellationToken cancellationToken,
        AllowanceCheck? allowance = null)
    {
        if (ProjectAccess.Current is { DatabaseName: not null } access && access.DatabaseName != name)
        {
            return ManagementErrors.NotFound;
        }

        var resolved = await project(transaction).ConfigureAwait(false);
        if (resolved is not TenantContext tenant)
        {
            return resolved switch
            {
                NotFound error => error,
                Forbidden error => error,
                TenantContext => throw new UnreachableException(),
            };
        }

        if (await IsProjectClosedAsync(transaction, tenant.ProjectId!, cancellationToken).ConfigureAwait(false))
        {
            return ManagementErrors.NotFound;
        }

        // A restore without a chosen name picks its candidate names only after its replay, so the precheck leaves the
        // names to the registration, which checks each candidate as it copies.
        if (name is not null && await CatalogTables.DatabaseNamedAsync(transaction, tenant.ProjectId!, name, cancellationToken).ConfigureAwait(false) is not null)
        {
            return ManagementErrors.NameTaken;
        }

        return allowance is not null && await DatabaseLimitAsync(transaction, tenant, allowance, cancellationToken).ConfigureAwait(false) is { } limit
            ? limit
            : tenant;
    }

    private static async ValueTask<Conflict?> DatabaseLimitAsync(IStorageTransaction transaction, TenantContext tenant, AllowanceCheck allowance, CancellationToken cancellationToken)
    {
        var allowed = allowance.Limit(tenant.Plan);
        if (allowed == long.MaxValue || PlatformDatabases.IsPlatform(tenant.OrganizationId))
        {
            return null;
        }

        return await CatalogTables.CountOrganizationDatabasesAsync(transaction, tenant.OrganizationId, cancellationToken).ConfigureAwait(false) >= allowed
            ? ManagementErrors.DatabaseLimit(PlanName(tenant.Plan), allowed)
            : null;
    }

    private static async ValueTask<TenantContext?> ProjectTenantAsync(
        IStorageTransaction transaction, Organization? organization, string projectId, CancellationToken cancellationToken) =>
        organization is not null
        && (ProjectAccess.Current is not { } access || access.ProjectId == projectId && access.OrganizationId == organization.Id)
        && await CatalogTables.OrganizationProjectAsync(transaction, organization.Id, projectId, cancellationToken).ConfigureAwait(false) is not null
            ? new TenantContext(organization.Id, organization.Plan, projectId)
            : null;

    private static async ValueTask<ProjectTenant> AuthorizedProjectAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, CancellationToken cancellationToken)
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

        return await OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false) is { } tenant
            ? tenant
            : ManagementErrors.NotFound;
    }

    private static async ValueTask<TenantContext?> DatabaseTenantAsync(
        IStorageTransaction transaction, TenantContext? project, string databaseId, CancellationToken cancellationToken) =>
        project is not null
        && (ProjectAccess.Current is not { DatabaseName: not null } access || access.DatabaseId == databaseId)
        && await CatalogTables.DatabaseAsync(transaction, project.ProjectId!, databaseId, cancellationToken).ConfigureAwait(false) is not null
            ? project with { DatabaseId = databaseId }
            : null;

    internal static async ValueTask<TenantContext?> AuthorizedDatabaseTenantAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        await DatabaseTenantAsync(
            transaction,
            await ProjectTenantAsync(transaction, await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false), projectId, cancellationToken)
                .ConfigureAwait(false),
            databaseId, cancellationToken)
            .ConfigureAwait(false);

    internal static async ValueTask<TenantContext?> DatabaseTenantUncheckedAsync(
        IStorageTransaction transaction, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        await DatabaseTenantAsync(
            transaction, await OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false), databaseId, cancellationToken)
            .ConfigureAwait(false);

    internal static ValueTask<ManagedDatabase?> DatabaseUncheckedAsync(
        IStorageTransaction transaction, string projectId, string databaseId, CancellationToken cancellationToken) =>
        CatalogTables.DatabaseAsync(transaction, projectId, databaseId, cancellationToken);

    internal static async ValueTask<PlanTier> PlanUncheckedAsync(IStorageTransaction transaction, string organizationId, CancellationToken cancellationToken) =>
        (await CatalogTables.OrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false))?.Plan ?? PlanTier.Hobby;

    private static async ValueTask<Organization?> GetOrganizationAsync(
        IStorageTransaction transaction, string userId, string organizationId, CancellationToken cancellationToken) =>
        await CatalogTables.OrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false) is { } organization
            && (organization.CreatedBy == userId || ProjectActor(userId, organizationId)) ? organization : null;

    internal static async ValueTask<Project?> GetProjectAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        ProjectAccess.Current is { } access && access.ProjectId != projectId
            || await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await CatalogTables.OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false);

    internal static async ValueTask<ManagedDatabase?> GetDatabaseAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        ProjectAccess.Current is { DatabaseName: not null } access && access.DatabaseId != databaseId
            || await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await CatalogTables.DatabaseAsync(transaction, projectId, databaseId, cancellationToken).ConfigureAwait(false);

    private static async ValueTask<bool> AnyAsync(IStorageTransaction transaction, string kind, CancellationToken cancellationToken) =>
        (await transaction.ListAsync(kind, afterId: null, limit: 1, MaxPageBytes, cancellationToken).ConfigureAwait(false)).Records.Count > 0;

    private static async ValueTask<T?> GetAsync<T>(
        IStorageTransaction transaction, string kind, string id, JsonTypeInfo<T> type, CancellationToken cancellationToken)
        where T : class
    {
        return await SystemCatalog.GetAsync(transaction, kind, id, type, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask PutAsync<T>(
        IStorageTransaction transaction, string kind, string id, T value, JsonTypeInfo<T> type, CancellationToken cancellationToken) =>
        await transaction.PutAsync(kind, id, EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(value, type), id, nested: true), cancellationToken).ConfigureAwait(false);

    private async ValueTask<T> ReadAsync<T>(Func<IStorageTransaction, ValueTask<T>> read, Func<Unavailable, T> unavailable, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var accepted = await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (accepted is not Done)
        {
            return accepted switch
            {
                Done => throw new UnreachableException(),
                Unavailable refused => unavailable(refused),
            };
        }

        return await ReadCatalogAsync(async transaction =>
            await CheckReadAsync(transaction, cancellationToken).ConfigureAwait(false) switch
            {
                Done => await read(transaction).ConfigureAwait(false),
                Unavailable stale => unavailable(stale),
            }, cancellationToken, file, line).ConfigureAwait(false);
    }

    public async ValueTask<CatalogCheck> CheckReadAsync(IStorageTransaction transaction, CancellationToken cancellationToken) =>
        await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false) != CatalogVersion
            ? ManagementErrors.CatalogUnsupported
            : new Done();

    private async ValueTask<T> ReadCatalogAsync<T>(Func<IStorageTransaction, ValueTask<T>> read, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken, file, line).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await read(transaction).ConfigureAwait(false);
        }
    }

    private async ValueTask<T> WriteAsync<T>(
        Func<IStorageTransaction, ValueTask<T>> write, Func<Unavailable, T> unavailable, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        return await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false) switch
        {
            Done => await WriteCatalogAsync(async transaction =>
                await CheckReadAsync(transaction, cancellationToken).ConfigureAwait(false) switch
                {
                    Done => await write(transaction).ConfigureAwait(false),
                    Unavailable refused => unavailable(refused),
                }, cancellationToken, file: file, line: line).ConfigureAwait(false),
            Unavailable refused => unavailable(refused),
        };
    }

    private async ValueTask<T> WriteCatalogAsync<T>(
        Func<IStorageTransaction, ValueTask<T>> write, CancellationToken cancellationToken, Func<T, bool>? keep = null,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await store.BeginTransactionAsync(write: true, cancellationToken, file, line).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var result = await write(transaction).ConfigureAwait(false);
            if (keep?.Invoke(result) != false)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }

    public async ValueTask<CatalogCheck> EnsureCatalogAsync(CancellationToken cancellationToken)
    {
        if (!catalogGate.Value.IsOpen)
        {
            return new Unavailable("this machine's catalog has not passed the restore gate", "catalog_unverified");
        }

        if (_catalogCurrent)
        {
            return new Done();
        }

        var accepted = await WriteCatalogAsync<CatalogCheck>(async transaction =>
        {
            var version = await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false);
            if (version == CatalogVersion)
            {
                return new Done();
            }

            if (version is not null || await CatalogTables.ExistAsync(transaction, cancellationToken).ConfigureAwait(false))
            {
                logger.ZLogError($"The management catalog has a format this build does not support; discard the affected data");
                return ManagementErrors.CatalogUnsupported;
            }

            await CatalogTables.CreateAsync(transaction, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Catalog, CatalogVersionId, CatalogVersion, Id, cancellationToken).ConfigureAwait(false);
            return new Done();
        }, cancellationToken, keep: static result => result is Done).ConfigureAwait(false);
        if (accepted is Done)
        {
            _catalogCurrent = true;
        }

        return accepted;
    }

    private string NewId() => EntityIds.New(time).ToString();

    private static string Key(string name) => CatalogTables.Key(name);

    private sealed record AllowanceCheck(Func<PlanTier, long> Limit);

    private sealed record AddedDatabase(ManagedDatabase Database, TenantContext Tenant, bool Existing = false);

    private sealed record DeletableDatabase(TenantContext Storage, ManagedDatabase Record);

    private readonly union ProjectTenant(TenantContext, NotFound, Forbidden);

    private readonly union AddDatabaseCheck(TenantContext, NotFound, Forbidden, Conflict);

    private readonly union AddDatabasePrecheck(TenantContext, NotFound, Forbidden, Conflict, Unavailable);

    private readonly union AddDatabaseResult(AddedDatabase, NotFound, Forbidden, Conflict, QuotaExceeded, Unavailable);

    private readonly union DeletableDatabaseCheck(DeletableDatabase, NotFound, Forbidden, Conflict);

    private readonly union DeletableDatabaseResult(DeletableDatabase, NotFound, Forbidden, Conflict, Unavailable);

    private readonly union UnregisterResult(DeletableDatabase, NotFound, Forbidden, Conflict);
}

internal sealed record DatabaseOwner(string OrganizationId, string ProjectId, string DatabaseId)
{
    public static DatabaseOwner Of(TenantContext database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new(database.OrganizationId, database.ProjectId!, database.DatabaseId!);
    }
}
