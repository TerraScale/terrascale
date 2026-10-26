using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management.Backups;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>
/// The catalog as a <see cref="ITenantDirectory"/> for organization API keys and scoped project access tokens.
/// </summary>
internal sealed partial class EngineManagementService
{
    public ValueTask<ResolveProjectDatabaseResult> ResolveProjectDatabaseAsync(string projectId, string database, CancellationToken cancellationToken) =>
        ReadAsync<ResolveProjectDatabaseResult>(async transaction =>
            await GetAsync(transaction, ProjectOrganizations, projectId, Id, cancellationToken).ConfigureAwait(false) is { } organizationId
                && await IsProjectBoundAsync(transaction, projectId, organizationId, cancellationToken).ConfigureAwait(false)
                && await FindDatabaseAsync(transaction, organizationId, projectId, database, cancellationToken).ConfigureAwait(false) is { } tenant
                && await GetAsync(transaction, Databases(projectId), tenant.DatabaseId!, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                    .ConfigureAwait(false) is { } record && record.Id == tenant.DatabaseId && record.ProjectId == projectId
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

    // Identity entries hold bounded ids; placement entries retain only the product's bounded name and region domain.
    private const int MaxCachedTenants = 4096;

    // Every data request resolves its database here. A lookup stays valid while the catalog has not committed since it
    // was read, so a point operation reads no catalog row and a long read on the storage threads cannot hold it up.
    // TS-647 step 5 replaces this: _system becomes a lattice database on every member, read from memory.
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), (long Sequence, TenantContext Tenant)> _tenants = new();

    // The same validity for the records the placement check reads on every data request (docs/cross-region.md).
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), (long Sequence, ManagedDatabase? Record)> _records = new();

    // Request admission must not queue catalog reads behind a noisy query while the catalog is unchanged.
    private readonly Lock _planGate = new();
    private readonly Dictionary<string, (Ulid Incarnation, long Sequence, PlanTier Plan)> _plans = [];

    // The same validity, keyed by database id, for the placement and registration checks replication makes on every head,
    // pull and stream.
    private readonly ConcurrentDictionary<(string Organization, string Project, string DatabaseId), (long Sequence, DatabaseRecordResult Value)> _placements = new();
    private readonly ConcurrentDictionary<(string Organization, string Project, string DatabaseId), (long Sequence, RegisteredDatabaseResult Value)> _registrations = new();

    // Serializes insertions so concurrent misses cannot grow a cache past MaxCachedTenants; never held across an await.
    private readonly Lock _cacheInsertion = new();

    // The running catalog read of each missed key at one commit sequence, removed when it ends, so bounded by concurrent requests.
    private readonly Lock _missGate = new();
    private readonly Dictionary<(string Kind, string Organization, string Project, string Database), (long Sequence, Task Read)> _misses = [];

    private readonly Counter<long> _cacheLookups = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.catalog.cache_lookups", "{lookup}",
        "Catalog cache lookups, by cache (tenants, records, plans, placements, registrations) and result (hit, miss).");

    // Whether the lookup hit, recorded under the cache's bounded name.
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

    // Every catalog commit invalidates the caches above, and every request of a fan-out (a thousand shape readers) then misses
    // together: concurrent misses of one key at one sequence share one read instead of each reading the same rows. The read runs
    // without the first caller's ambient context and cancellation; a caller inside a commit transaction reads its own.
    private async ValueTask<T> ShareMissAsync<T>(
        string kind, (string, string, string) key, long sequence, Func<CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken)
    {
        if (CommitTransaction.Value is not null)
        {
            return await read(cancellationToken).ConfigureAwait(false);
        }

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

    // A catalog read of database's identity, reused while the catalog has not committed since the read began.
    private async ValueTask<T> ReadCachedAsync<T>(
        string name, ConcurrentDictionary<(string, string, string), (long Sequence, T Value)> cache, TenantContext database,
        Func<IStorageTransaction, ValueTask<T>> read, Func<Unavailable, T> unavailable, Func<T, bool> admit, CancellationToken cancellationToken)
    {
        RequireNoCommit();
        // Taken before the read: a commit racing it leaves the entry stale-tagged, never an older snapshot under a newer tag.
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

    // At most ten 63-character strings, about 2 KiB per entry. Larger remote records are answered without retaining them.
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

    // The catalog replicates to every region (docs/terrabase/multi-region-replication.md section 15): a database created
    // elsewhere and placed in this region gets its local file on its first request or first replicated group, and serves
    // before its first documents arrive. A database placed elsewhere gets none (the placement check answers for it).
    // Recheck a file created here after initialization: a concurrent deletion may have applied before the file existed.
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

    public async ValueTask<ProvisionDatabaseResult> CreateDatabaseAsync(string organizationId, string projectId, string database, CancellationToken cancellationToken)
    {
        return await AddDatabaseAsync(
            async transaction => await OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false) is { } tenant
                ? tenant
                : ManagementErrors.NotFound,
            database, sourceId: null, branchedFrom: null, cancellationToken)
            .ConfigureAwait(false) switch
        {
            AddedDatabase added => new ProvisionedDatabase(added.Tenant, Created: true),
            QuotaExceeded => throw new UnreachableException(),
            // The name is taken: the database exists, unless it was deleted again meanwhile.
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
                    : await GetAsync(transaction, Databases(database.ProjectId), database.DatabaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                        .ConfigureAwait(false) is { } record
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
            // Check this snapshot before decoding records, even if the primary accepted an earlier version.
            if (!Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false)))
            {
                return UnreadableCatalog;
            }

            // At most 100 identities, each with three ULIDs: under 22 KiB of character data. Storage scan pages are
            // separately bounded to 100 documents and 1 MiB. Release the reader before WAN requests or cleanup writes.
            var records = new List<TenantContext>(MemberPage);
            string? afterProject = after?.ProjectId;
            if (afterProject is not null
                && await GetAsync(transaction, ProjectOrganizations, afterProject, Id, cancellationToken).ConfigureAwait(false) is { } organization
                && await ReadProjectAsync(afterProject, organization, after!.DatabaseId).ConfigureAwait(false))
            {
                return new TenantDatabasePage(records, records[^1]);
            }

            // ponytail: scan the paged catalog; index by home region once catalogs hold thousands of databases.
            DocumentPage projects;
            do
            {
                projects = await transaction.ListAsync(ProjectOrganizations, afterProject, MemberPage, MaxPageBytes, cancellationToken).ConfigureAwait(false);
                foreach (var project in projects.Documents)
                {
                    if (await ReadProjectAsync(project.Id, JsonSerializer.Deserialize(project.Json.Span, Id)!, afterDatabase: null).ConfigureAwait(false))
                    {
                        return new TenantDatabasePage(records, records[^1]);
                    }
                }

                afterProject = projects.Documents.Count > 0 ? projects.Documents[^1].Id : afterProject;
            }
            while (projects.HasMore);

            return new TenantDatabasePage(records, Next: null);

            async ValueTask<bool> ReadProjectAsync(string project, string organizationId, string? afterDatabase)
            {
                DocumentPage databases;
                do
                {
                    databases = await transaction.ListAsync(Databases(project), afterDatabase, MemberPage, MaxPageBytes, cancellationToken).ConfigureAwait(false);
                    foreach (var document in databases.Documents)
                    {
                        var database = EntityPayload.Read(document, TerraBaseJsonContext.Default.ManagedDatabase)!;
                        if (database.Home(options.Region) == homeRegion
                            && !await DatabasePurge.IsDeletedAsync(transaction, database.Id, cancellationToken).ConfigureAwait(false))
                        {
                            records.Add(new TenantContext(organizationId, PlanTier.Hobby, project, database.Id));
                            if (records.Count == MemberPage)
                            {
                                return true;
                            }
                        }
                    }

                    afterDatabase = databases.Documents.Count > 0 ? databases.Documents[^1].Id : afterDatabase;
                }
                while (databases.HasMore);

                return false;
            }
        }, static error => error, cancellationToken);

    public async ValueTask<AdoptDatabaseResult> AdoptDatabaseAsync(TenantContext database, string name, DatabasePlacement placement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(placement);
        if (database.ProjectId is null || database.DatabaseId is null || !DatabaseName.TryParse(name, out _))
        {
            return ManagementErrors.NotFound;
        }

        // Every database id this catalog ever registered keeps its entry, deleted ones as tombstones.
        var history = await ReadAsync<AdoptDatabaseResult>(
            async transaction => !Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false))
                ? UnreadableCatalog
                : !await DatabasePurge.IsDeletedAsync(transaction, database.DatabaseId, cancellationToken).ConfigureAwait(false)
                    && await transaction.GetVersionAsync(EngineManagedBackupService.BackedUpDatabases, database.DatabaseId, cancellationToken)
                        .ConfigureAwait(false) is null ? new Done() : CatalogDatabaseDeleted,
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

        if (placement.Includes(options.Region))
        {
            return await AddDatabaseAsync(project, name, sourceId: null, branchedFrom: null, cancellationToken, id: database.DatabaseId, placement: placement)
                .ConfigureAwait(false) switch
            {
                AddedDatabase => new Done(),
                QuotaExceeded => throw new UnreachableException(),
                Conflict error when error == ManagementErrors.NameTaken => error,
                Unavailable error => error,
                NotFound or Forbidden or Conflict => ManagementErrors.NotFound,
            };
        }

        // Placed elsewhere: only the record, no file and no backups, so requests here can be told where it is served.
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
                await PutAsync(transaction, DatabaseNames(database.ProjectId), name, database.DatabaseId, Id, cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, Databases(database.ProjectId), database.DatabaseId, record, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                    .ConfigureAwait(false);
                await TouchOrganizationAsync(transaction, tenant.OrganizationId, cancellationToken).ConfigureAwait(false);
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

            if (await GetAsync(transaction, Databases(database.ProjectId), database.DatabaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                .ConfigureAwait(false) is not { } record || placement.Version <= record.PlacementVersion || record.Home(options.Region) == options.Region)
            {
                // The home region decides its databases' placement; nothing older replaces a newer one.
                return new PlacementUpdate(Updated: false);
            }

            await PutAsync(transaction, Databases(database.ProjectId), database.DatabaseId, record with
            {
                HomeRegion = placement.Home,
                ReplicaRegions = placement.Replicas.Count == 0 ? null : [.. placement.Replicas],
                PlacementVersion = placement.Version,
                AllRegions = placement.AllRegions,
            }, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false);
            // Backed up where it is served; a removal leaves no tombstone, so the database can come back with its placement.
            if (placement.Includes(options.Region))
            {
                await PutAsync(transaction, EngineManagedBackupService.BackedUpDatabases, database.DatabaseId, DatabaseOwner.Of(database),
                    TerraBaseJsonContext.Default.DatabaseOwner, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _ = await transaction.DeleteAsync(EngineManagedBackupService.BackedUpDatabases, database.DatabaseId, cancellationToken).ConfigureAwait(false);
            }

            await TouchOrganizationAsync(transaction, database.OrganizationId, cancellationToken).ConfigureAwait(false);
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

        // Never a database at home here: only its home region deletes it.
        async ValueTask<(TenantContext Storage, ManagedDatabase Record)?> ElsewhereAsync(IStorageTransaction transaction)
        {
            return await OrganizationProjectAsync(transaction, database.OrganizationId, database.ProjectId, cancellationToken).ConfigureAwait(false) is { } tenant
                && await GetAsync(transaction, Databases(database.ProjectId), database.DatabaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                    .ConfigureAwait(false) is { } record
                && record.Home(options.Region) != options.Region
                    ? (tenant with { DatabaseId = record.Id }, record)
                    : null;
        }

        // A catalog this build cannot use forgets nothing.
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
                return false; // The engine callback declines recording the deletion intent.
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
            && await GetAsync(transaction, Databases(projectId), tenant.DatabaseId!, TerraBaseJsonContext.Default.ManagedDatabase, token).ConfigureAwait(false) is { } record
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
                ? await PlanUncheckedAsync(transaction, organizationId, token).ConfigureAwait(false)
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
                        && (await GetAsync(transaction, Databases(database.ProjectId), database.DatabaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                            .ConfigureAwait(false))?.PlacedIn(options.Region) == true,
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
                        && (await GetAsync(transaction, Databases(database.ProjectId), database.DatabaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                            .ConfigureAwait(false))?.PlacedIn(options.Region) == true)
                        || (!await DatabasePurge.IsDeletedAsync(transaction, database.DatabaseId, cancellationToken).ConfigureAwait(false)
                            && await GetAsync(transaction, PendingDatabaseCleanup, database.DatabaseId, TerraBaseJsonContext.Default.TenantContext, cancellationToken)
                                .ConfigureAwait(false) is not null),
                static error => error, cancellationToken);
    }

    private static Unavailable UnreadableCatalog => ManagementErrors.CatalogUnsupported;

    private static readonly Conflict CatalogDatabaseDeleted = new("a database deleted here is not brought back", "database_deleted");

    // Unsupported catalogs are never interpreted or migrated.
    private static bool Interprets(string? version) => version == CatalogVersion;

    /// <summary>
    /// Whether an organization, one of its projects and one database of that project (each optional after the first)
    /// exist, inside a catalog transaction and without a membership check: the scope of an API key.
    /// </summary>
    internal static async ValueTask<bool> ScopeExistsUncheckedAsync(
        IStorageTransaction transaction, string organizationId, string? projectId, string? databaseId, CancellationToken cancellationToken)
    {
        if (projectId is null)
        {
            return databaseId is null
                && await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken)
                    .ConfigureAwait(false) is not null;
        }

        return await OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false) is not null
            && (databaseId is null
                || await GetAsync(transaction, Databases(projectId), databaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                    .ConfigureAwait(false) is not null);
    }

    private static async ValueTask<TenantContext?> FindDatabaseAsync(
        IStorageTransaction transaction, string organizationId, string projectId, string database, CancellationToken cancellationToken) =>
        await OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false) is { } project
        && (EntityIds.IsValid(database) ? database : await NameHolderAsync(transaction, projectId, database, cancellationToken).ConfigureAwait(false)) is { } databaseId
        && await GetAsync(transaction, Databases(projectId), databaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is { } record
        && record.ProjectId == projectId && (record.Id == database || record.Name == database)
        && !await DatabasePurge.IsDeletedAsync(transaction, databaseId, cancellationToken).ConfigureAwait(false)
            ? project with { DatabaseId = databaseId }
            : null;

    // The tenant of a project of an organization, without a user: API keys act for their organization.
    private static async ValueTask<TenantContext?> OrganizationProjectAsync(
        IStorageTransaction transaction, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await ProjectTenantAsync(
            transaction,
            await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false),
            projectId,
            cancellationToken).ConfigureAwait(false);
}
