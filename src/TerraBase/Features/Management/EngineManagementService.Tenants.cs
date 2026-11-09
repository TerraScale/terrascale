using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management.Backups;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    public ValueTask<ResolveProjectDatabaseResult> ResolveProjectDatabaseAsync(string projectId, string database, CancellationToken cancellationToken) =>
        ReadAsync<ResolveProjectDatabaseResult>(async transaction =>
            await CatalogTables.ProjectOrganizationAsync(transaction, projectId, cancellationToken).ConfigureAwait(false) is { } organizationId
                && await IsProjectRegisteredAsync(transaction, projectId, organizationId, cancellationToken).ConfigureAwait(false)
                && await FindDatabaseAsync(transaction, organizationId, projectId, database, cancellationToken).ConfigureAwait(false) is { } tenant
                && await CatalogTables.DatabaseAsync(transaction, projectId, tenant.DatabaseId!, cancellationToken).ConfigureAwait(false) is { } record
                    ? new ResolvedProjectDatabase(tenant, record.Placement(options.Region))
                    : ManagementErrors.NotFound,
            static error => error, cancellationToken);

    public async ValueTask<EnsureLocalDatabaseResult> EnsureLocalDatabaseAsync(TenantContext database, CancellationToken cancellationToken) =>
        await RegisteredAsync(database, cancellationToken).ConfigureAwait(false) switch
        {
            true => await InitializeLocalAsync(database, cancellationToken).ConfigureAwait(false),
            false => ManagementErrors.NotFound,
            Unavailable error => error,
        };

    private const int MaxCachedTenants = 4096;

    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), (long Sequence, TenantContext Tenant)> _tenants = new();

    // The records the placement check reads on every data request keep the validity of docs/cross-region.md.
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), (long Sequence, ManagedDatabase? Record)> _records = new();

    private readonly Lock _planGate = new();
    private readonly Dictionary<string, (Ulid Incarnation, long Sequence, PlanTier Plan)> _plans = [];

    private readonly ConcurrentDictionary<(string Organization, string Project, string DatabaseId), (long Sequence, DatabaseRecordResult Value)> _placements = new();
    private readonly ConcurrentDictionary<(string Organization, string Project, string DatabaseId), (long Sequence, RegisteredDatabaseResult Value)> _registrations = new();

    private readonly Lock _cacheInsertion = new();

    private readonly Lock _missGate = new();
    private readonly Dictionary<(string Kind, string Organization, string Project, string Database), (long Sequence, Task Read)> _misses = [];

    private readonly Counter<long> _cacheLookups = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.catalog.cache_lookups", "{lookup}",
        "Catalog cache lookups, by cache (tenants, records, plans, placements, registrations) and result (hit, miss).");

    private bool Looked(string cache, bool hit)
    {
        _cacheLookups.Add(1, new KeyValuePair<string, object?>("cache", cache), new KeyValuePair<string, object?>("result", hit ? "hit" : "miss"));
        return hit;
    }

    public async ValueTask<FindDatabaseResult> FindDatabaseAsync(string organizationId, string projectId, string database, CancellationToken cancellationToken)
    {
        var sequence = (await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)).CommitSequence;
        var key = (organizationId, projectId, database);
        if (Looked("tenants", _tenants.TryGetValue(key, out var cached) && cached.Sequence == sequence))
        {
            return cached.Tenant;
        }

        var found = await ShareMissAsync("tenant", key, sequence, token => FindUncachedAsync(organizationId, projectId, database, token), cancellationToken)
            .ConfigureAwait(false);
        if (found is TenantContext tenant)
        {
            Cache(_tenants, key, sequence, tenant);
        }

        return found;
    }

    private void Cache<T>(ConcurrentDictionary<(string, string, string), (long, T)> cache, (string, string, string) key, long sequence, T value)
    {
        if (key.Item1.Length > 63 || key.Item2.Length > 63 || key.Item3.Length > 63)
        {
            return;
        }

        lock (_cacheInsertion)
        {
            if (cache.Count >= MaxCachedTenants)
            {
                cache.Clear();
            }

            cache[key] = (sequence, value);
        }
    }

    private async ValueTask<T> ShareMissAsync<T>(
        string kind, (string, string, string) key, long sequence, Func<CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken)
    {
        var id = (kind, key.Item1, key.Item2, key.Item3);
        Task<T> shared;
        lock (_missGate)
        {
            if (_misses.TryGetValue(id, out var running) && running.Sequence == sequence)
            {
                shared = (Task<T>)running.Read;
            }
            else
            {
                var flow = ExecutionContext.IsFlowSuppressed() ? (AsyncFlowControl?)null : ExecutionContext.SuppressFlow();
                try
                {
                    shared = Task.Run(async () => await read(CancellationToken.None).ConfigureAwait(false), CancellationToken.None);
                }
                finally
                {
                    flow?.Undo();
                }

                _misses[id] = (sequence, shared);
                _ = shared.ContinueWith(_ =>
                {
                    lock (_missGate)
                    {
                        if (_misses.TryGetValue(id, out var current) && current.Read == shared)
                        {
                            _misses.Remove(id);
                        }
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        return await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> ReadCachedAsync<T>(
        string name, ConcurrentDictionary<(string, string, string), (long Sequence, T Value)> cache, TenantContext database,
        Func<IStorageTransaction, ValueTask<T>> read, Func<Unavailable, T> unavailable, Func<T, bool> admit, CancellationToken cancellationToken)
    {
        var sequence = (await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)).CommitSequence;
        var key = (database.OrganizationId, database.ProjectId!, database.DatabaseId!);
        if (Looked(name, cache.TryGetValue(key, out var cached) && cached.Sequence == sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cached.Value;
        }

        var value = await ReadAsync(read, unavailable, cancellationToken).ConfigureAwait(false);
        if (admit(value))
        {
            Cache(cache, key, sequence, value);
        }

        return value;
    }

    private static bool Admits((string Name, DatabasePlacement Placement)? record) =>
        record is not { } value || (value.Name.Length <= 63 && TerraBaseOptions.IsRegion(value.Placement.Home)
            && value.Placement.Replicas.Count <= DatabaseRegions.MaxReplicas && value.Placement.Replicas.All(TerraBaseOptions.IsRegion));

    private async ValueTask<FindDatabaseResult> FindUncachedAsync(string organizationId, string projectId, string database, CancellationToken cancellationToken)
    {
        var found = await ReadAsync<FindDatabaseResult>(
            async transaction => await FindDatabaseAsync(transaction, organizationId, projectId, database, cancellationToken).ConfigureAwait(false) is { } tenant
                ? tenant
                : ManagementErrors.NotFound,
            static error => error, cancellationToken).ConfigureAwait(false);
        return found switch
        {
            TenantContext tenant => await InitializeLocalAsync(tenant, cancellationToken).ConfigureAwait(false) switch
            {
                Done => tenant,
                NotFound error => error,
                Unavailable error => error,
            },
            NotFound error => error,
            Unavailable error => error,
        };
    }

    // The catalog replicates to every region (docs/terrabase/multi-region-replication.md section 15).
    private async ValueTask<EnsureLocalDatabaseResult> InitializeLocalAsync(TenantContext tenant, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new Done();
        }

        var placed = await RegisteredAsync(tenant, cancellationToken).ConfigureAwait(false);
        if (placed is not true)
        {
            return placed switch
            {
                Unavailable error => error,
                _ => new Done(),
            };
        }

        if (await engine.CreateDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false))
        {
            var registration = await RegisteredAsync(tenant, cancellationToken).ConfigureAwait(false);
            if (registration is not bool registered)
            {
                return registration switch
                {
                    Unavailable error => error,
                    bool => throw new UnreachableException(),
                };
            }

            if (!registered)
            {
                if (await engine.OpenDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false) is { } created)
                {
                    _ = await engine.DeleteDatabaseAsync(tenant, cancellationToken, incarnation: created.Incarnation).ConfigureAwait(false);
                }

                return ManagementErrors.NotFound;
            }
        }

        return new Done();
    }

    public async ValueTask<HomeDatabaseCountResult> CountOrganizationDatabasesHomedAsync(string organizationId, CancellationToken cancellationToken) =>
        await ReadAsync<HomeDatabaseCountResult>(
            async transaction => await CatalogTables.CountOrganizationDatabasesHomedAsync(transaction, organizationId, options.Region, cancellationToken).ConfigureAwait(false),
            static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ProvisionDatabaseResult> CreateDatabaseAsync(string organizationId, string projectId, string database, CancellationToken cancellationToken)
    {
        return await AddDatabaseAsync(
            organizationId,
            async transaction => await OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false) is { } tenant
                ? tenant
                : ManagementErrors.NotFound,
            database, sourceId: null, branchedFrom: null, cancellationToken)
            .ConfigureAwait(false) switch
        {
            AddedDatabase added => new ProvisionedDatabase(added.Tenant, Created: true),
            QuotaExceeded => throw new UnreachableException(),
            Conflict error when error == ManagementErrors.NameTaken => await FindDatabaseAsync(organizationId, projectId, database, cancellationToken).ConfigureAwait(false) switch
            {
                TenantContext existing => new ProvisionedDatabase(existing, Created: false),
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            Conflict { Code: ManagementErrors.DatabaseLimitCode } error => error,
            Unavailable error => error,
            NotFound or Forbidden or Conflict => ManagementErrors.NotFound,
        };
    }

    public ValueTask<DatabaseRecordResult> DatabaseRecordAsync(TenantContext database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.ProjectId is null || database.DatabaseId is null
            ? ValueTask.FromResult<DatabaseRecordResult>(ManagementErrors.NotFound)
            : ReadCachedAsync<DatabaseRecordResult>("placements", _placements, database, async transaction =>
                !Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false))
                    ? UnreadableCatalog
                    : await CatalogTables.DatabaseAsync(transaction, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false) is { } record
                        ? new TenantDatabaseRecord(record.Name, record.Placement(options.Region))
                        : ManagementErrors.NotFound,
                static error => error, static value => value switch
                {
                    TenantDatabaseRecord record => Admits((record.Name, record.Placement)),
                    NotFound => true,
                    Unavailable => false,
                }, cancellationToken);
    }

    public ValueTask<ReadDatabaseRecordsResult> ReadDatabaseRecordsAsync(string homeRegion, TenantContext? after, CancellationToken cancellationToken) =>
        ReadAsync<ReadDatabaseRecordsResult>(async transaction =>
        {
            if (!Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false)))
            {
                return UnreadableCatalog;
            }

            var records = await CatalogTables.DatabasesHomedAsync(transaction, homeRegion, options.Region, after, CatalogPage, cancellationToken).ConfigureAwait(false);
            return new TenantDatabasePage(records, records.Count == CatalogPage ? records[^1] : null);
        }, static error => error, cancellationToken);

    public async ValueTask<AdoptDatabaseResult> AdoptDatabaseAsync(TenantContext database, string name, DatabasePlacement placement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(placement);
        if (database.ProjectId is null || database.DatabaseId is null || !DatabaseName.TryParse(name, out _))
        {
            return ManagementErrors.NotFound;
        }

        var history = await ReadAsync<AdoptDatabaseResult>(
            async transaction => !Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false))
                ? UnreadableCatalog
                : !await DatabasePurge.IsDeletedAsync(transaction, database.DatabaseId, cancellationToken).ConfigureAwait(false)
                    && await transaction.GetVersionAsync(EngineManagedBackupService.BackedUpDatabases, database.DatabaseId, cancellationToken)
                        .ConfigureAwait(false) is null
                    && await transaction.GetVersionAsync(RetiredDatabases, database.DatabaseId, cancellationToken).ConfigureAwait(false) is null
                    ? new Done() : CatalogDatabaseDeleted,
            static error => error, cancellationToken).ConfigureAwait(false);
        if (history is not Done)
        {
            return history switch
            {
                Conflict error => error,
                NotFound error => error,
                Unavailable error => error,
                Done => throw new UnreachableException(),
            };
        }

        async ValueTask<ProjectTenant> project(IStorageTransaction transaction)
        {
            return await OrganizationProjectAsync(transaction, database.OrganizationId, database.ProjectId!, cancellationToken).ConfigureAwait(false) is { } tenant
                ? tenant
                : ManagementErrors.NotFound;
        }

        // Only the home holds a database file; a replica region records it and pulls its pages (docs/terrabase/region-replicas.md).
        if (placement.Home == options.Region)
        {
            return await AddDatabaseAsync(database.OrganizationId, project, name, sourceId: null, branchedFrom: null, cancellationToken, id: database.DatabaseId, placement: placement)
                .ConfigureAwait(false) switch
            {
                AddedDatabase => new Done(),
                QuotaExceeded => throw new UnreachableException(),
                Conflict error when error == ManagementErrors.NameTaken => error,
                Unavailable error => error,
                NotFound or Forbidden or Conflict => ManagementErrors.NotFound,
            };
        }

        var record = new ManagedDatabase(
            database.DatabaseId, database.ProjectId, name, null, time.GetUtcNow(), placement.Home, placement.Replicas.Count == 0 ? null : [.. placement.Replicas], placement.Version,
            placement.AllRegions);
        return await WriteAsync<AdoptDatabaseResult>(async transaction =>
        {
            if (await DatabasePurge.IsDeletedAsync(transaction, database.DatabaseId, cancellationToken).ConfigureAwait(false))
            {
                return CatalogDatabaseDeleted;
            }

            var check = await CanAddDatabaseAsync(transaction, project, name, cancellationToken).ConfigureAwait(false);
            if (check is TenantContext tenant)
            {
                await CatalogTables.PutDatabaseAsync(transaction, record, cancellationToken).ConfigureAwait(false);

            }

            return check switch
            {
                TenantContext => new Done(),
                Conflict error when error == ManagementErrors.NameTaken => error,
                NotFound or Forbidden or Conflict => ManagementErrors.NotFound,
            };
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<UpdatePlacementResult> UpdatePlacementAsync(TenantContext database, DatabasePlacement placement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(placement);
        if (database.ProjectId is null || database.DatabaseId is null)
        {
            return new PlacementUpdate(Updated: false);
        }

        return await WriteAsync<UpdatePlacementResult>(async transaction =>
        {
            if (!Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false)))
            {
                return UnreadableCatalog;
            }

            if (await CatalogTables.DatabaseAsync(transaction, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false) is not { } record
                || placement.Version <= record.PlacementVersion || record.Home(options.Region) == options.Region)
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
            if (placement.Home == options.Region)
            {
                await PutAsync(transaction, EngineManagedBackupService.BackedUpDatabases, database.DatabaseId, DatabaseOwner.Of(database),
                    TerraBaseJsonContext.Default.DatabaseOwner, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _ = await transaction.DeleteAsync(EngineManagedBackupService.BackedUpDatabases, database.DatabaseId, cancellationToken).ConfigureAwait(false);
            }

            return new PlacementUpdate(Updated: true);
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ForgetDatabaseResult> ForgetDatabaseAsync(TenantContext database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.ProjectId is null || database.DatabaseId is null)
        {
            return new DatabaseForgotten(Forgotten: false);
        }

        async ValueTask<(TenantContext Storage, ManagedDatabase Record)?> ElsewhereAsync(IStorageTransaction transaction)
        {
            return await OrganizationProjectAsync(transaction, database.OrganizationId, database.ProjectId, cancellationToken).ConfigureAwait(false) is { } tenant
                && await CatalogTables.DatabaseAsync(transaction, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false) is { } record
                && record.Home(options.Region) != options.Region
                    ? (tenant with { DatabaseId = record.Id }, record)
                    : null;
        }

        var found = await ReadAsync<ForgetTargetCheck>(async transaction =>
            !Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false))
                ? UnreadableCatalog
                : await ElsewhereAsync(transaction).ConfigureAwait(false) is { } elsewhere ? new ForgetTarget(elsewhere.Storage) : ManagementErrors.NotFound,
            static error => error, cancellationToken).ConfigureAwait(false);
        if (found is not ForgetTarget target)
        {
            return found switch
            {
                NotFound => new DatabaseForgotten(Forgotten: false),
                Unavailable error => error,
                ForgetTarget => throw new UnreachableException(),
            };
        }

        ForgetDatabaseResult disposition = new DatabaseForgotten(Forgotten: false);
        var deleted = await DeleteRecordedAsync(target.Storage, async (transaction, token) =>
        {
            if (!Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, token).ConfigureAwait(false)))
            {
                disposition = UnreadableCatalog;
                return false;
            }

            if (await ElsewhereAsync(transaction).ConfigureAwait(false) is not { } current)
            {
                return false;
            }

            _ = await UnregisterRecordAsync(transaction, current.Storage, current.Record, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return deleted switch
        {
            bool forgotten => forgotten ? new DatabaseForgotten(Forgotten: true) : disposition,
            Unavailable refused => refused,
        };
    }

    private sealed record ForgetTarget(TenantContext Storage);
    private readonly union ForgetTargetCheck(ForgetTarget, NotFound, Unavailable);

    public async ValueTask<FindDatabaseRecordResult> FindDatabaseRecordAsync(string organizationId, string projectId, string name, CancellationToken cancellationToken)
    {
        var sequence = (await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)).CommitSequence;
        var key = (organizationId, projectId, name);
        if (Looked("records", _records.TryGetValue(key, out var cached) && cached.Sequence == sequence))
        {
            return cached.Record is { } known ? known : ManagementErrors.NotFound;
        }

        var found = await ShareMissAsync("record", key, sequence, token => ReadAsync<FindDatabaseRecordResult>(async transaction =>
            await FindDatabaseAsync(transaction, organizationId, projectId, name, token).ConfigureAwait(false) is { } tenant
            && await CatalogTables.DatabaseAsync(transaction, projectId, tenant.DatabaseId!, token).ConfigureAwait(false) is { } record
                ? record
                : ManagementErrors.NotFound,
            static error => error, token), cancellationToken).ConfigureAwait(false);
        if (found is Unavailable)
        {
            return found;
        }

        var cachedRecord = found is ManagedDatabase database ? database : null;
        var validKey = EntityIds.IsValid(projectId) && (EntityIds.IsValid(name) || DatabaseName.TryParse(name, out _));
        if (validKey && (cachedRecord is null || Admits((cachedRecord.Name, cachedRecord.Placement(options.Region)))))
        {
            Cache(_records, key, sequence, cachedRecord);
        }
        return found;
    }

    public async ValueTask<TenantPlanResult> PlanAsync(string organizationId, CancellationToken cancellationToken)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var incarnation = store.Incarnation;
        var sequence = store.CommitSequence;
        lock (_planGate)
        {
            if (Looked("plans", _plans.TryGetValue(organizationId, out var cached) && cached.Incarnation == incarnation && cached.Sequence == sequence))
            {
                return cached.Plan;
            }
        }

        var result = await ShareMissAsync("plan", (organizationId, incarnation.ToString(), ""), sequence, token => ReadAsync<TenantPlanResult>(async transaction =>
            Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, token).ConfigureAwait(false))
                ? await AdmissionPlanAsync(transaction, organizationId, token).ConfigureAwait(false)
                : UnreadableCatalog,
            static error => error, token), cancellationToken).ConfigureAwait(false);
        if (result is PlanTier plan && store.Incarnation == incarnation && store.CommitSequence == sequence)
        {
            lock (_planGate)
            {
                if (_plans.Count == MaxCachedTenants)
                {
                    _plans.Clear();
                }

                _plans[organizationId] = (incarnation, sequence, plan);
            }
        }

        return result;
    }

    public ValueTask<TenantCatalogResult> CheckCatalogAsync(CancellationToken cancellationToken) =>
        ReadAsync<TenantCatalogResult>(async transaction =>
            Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false))
                ? new Done() : UnreadableCatalog,
            static error => error, cancellationToken);

    public ValueTask<RegisteredDatabaseResult> RegisteredAsync(TenantContext database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.ProjectId is null || database.DatabaseId is null
            ? ValueTask.FromResult<RegisteredDatabaseResult>(false)
            : ReadCachedAsync<RegisteredDatabaseResult>("registrations", _registrations, database, async transaction =>
                !Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false))
                    ? UnreadableCatalog
                    : await ScopeExistsUncheckedAsync(transaction, database.OrganizationId, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false)
                        && (await CatalogTables.DatabaseAsync(transaction, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false))
                            ?.PlacedIn(options.Region) == true,
                static error => error, static value => value is bool, cancellationToken);
    }

    public ValueTask<ReplicatesDatabaseResult> ReplicatesAsync(TenantContext database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.ProjectId is null || database.DatabaseId is null
            ? ValueTask.FromResult<ReplicatesDatabaseResult>(false)
            : ReadAsync<ReplicatesDatabaseResult>(async transaction =>
                !Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false))
                    ? UnreadableCatalog
                    : (await ScopeExistsUncheckedAsync(transaction, database.OrganizationId, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false)
                        && (await CatalogTables.DatabaseAsync(transaction, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false))
                            ?.PlacedIn(options.Region) == true)
                        || (!await DatabasePurge.IsDeletedAsync(transaction, database.DatabaseId, cancellationToken).ConfigureAwait(false)
                            && await CatalogTables.PendingCleanupAsync(transaction, database.DatabaseId, cancellationToken).ConfigureAwait(false) is not null),
                static error => error, cancellationToken);
    }

    private static Unavailable UnreadableCatalog => ManagementErrors.CatalogUnsupported;

    private static readonly Conflict CatalogDatabaseDeleted = new("a database deleted here is not brought back", "database_deleted");

    private static bool Interprets(string? version) => version == CatalogVersion;

    internal static async ValueTask<bool> ScopeExistsUncheckedAsync(
        IStorageTransaction transaction, string organizationId, string? projectId, string? databaseId, CancellationToken cancellationToken)
    {
        if (projectId is null)
        {
            return databaseId is null
                && await CatalogTables.OrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false) is not null;
        }

        return await OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false) is not null
            && (databaseId is null
                || await CatalogTables.DatabaseAsync(transaction, projectId, databaseId, cancellationToken).ConfigureAwait(false) is not null);
    }

    private static async ValueTask<TenantContext?> FindDatabaseAsync(
        IStorageTransaction transaction, string organizationId, string projectId, string database, CancellationToken cancellationToken) =>
        await OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false) is { } project
        && (EntityIds.IsValid(database) ? database : await CatalogTables.DatabaseNamedAsync(transaction, projectId, database, cancellationToken).ConfigureAwait(false)) is { } databaseId
        && await CatalogTables.DatabaseAsync(transaction, projectId, databaseId, cancellationToken).ConfigureAwait(false) is { } record
        && (record.Id == database || record.Name == database)
            ? project with { DatabaseId = databaseId }
            : null;

    private static async ValueTask<TenantContext?> OrganizationProjectAsync(
        IStorageTransaction transaction, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await ProjectTenantAsync(
            transaction,
            await CatalogTables.OrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false),
            projectId,
            cancellationToken).ConfigureAwait(false);
}
