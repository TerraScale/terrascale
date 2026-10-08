using System.Collections.Concurrent;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Management.Backups;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>
/// The catalog as a <see cref="ITenantDirectory"/> for organization API keys and scoped project access tokens.
/// </summary>
internal sealed partial class EngineManagementService
{
    public ValueTask<(TenantContext Database, DatabasePlacement Placement)?> ResolveProjectDatabaseAsync(string projectId, string database, CancellationToken cancellationToken) =>
        ReadAsync<(TenantContext, DatabasePlacement)?>(async transaction =>
            await GetAsync(transaction, ProjectOrganizations, projectId, Id, cancellationToken).ConfigureAwait(false) is { } organizationId
                && await IsProjectBoundAsync(transaction, projectId, organizationId, cancellationToken).ConfigureAwait(false)
                && await FindDatabaseAsync(transaction, organizationId, projectId, database, cancellationToken).ConfigureAwait(false) is { } tenant
                && await GetAsync(transaction, Databases(projectId), tenant.DatabaseId!, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                    .ConfigureAwait(false) is { } record && record.Id == tenant.DatabaseId && record.ProjectId == projectId && record.Name == database
                    ? (tenant, record.Placement(options.Region))
                    : null,
            cancellationToken);

    public async ValueTask<bool> EnsureLocalDatabaseAsync(TenantContext database, CancellationToken cancellationToken) =>
        await RegisteredAsync(database, cancellationToken).ConfigureAwait(false) is true
            && await InitializeFollowerAsync(database, cancellationToken).ConfigureAwait(false);

    // Bounded by count; each entry holds three ids of at most a few hundred bytes.
    private const int MaxCachedTenants = 4096;

    // Every data request resolves its database here. A lookup stays valid while the catalog has not committed since it
    // was read, so a point operation reads no catalog row and a long read on the storage threads cannot hold it up.
    // TS-647 step 5 replaces this: _system becomes a lattice database on every member, read from memory.
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), (long Sequence, TenantContext Tenant)> _tenants = new();

    // The same validity for the records the placement check reads on every data request (docs/cross-region.md).
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), (long Sequence, ManagedDatabase? Record)> _records = new();

    public async ValueTask<TenantContext?> FindDatabaseAsync(string organizationId, string projectId, string database, CancellationToken cancellationToken)
    {
        var sequence = (await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)).CommitSequence;
        var key = (organizationId, projectId, database);
        if (_tenants.TryGetValue(key, out var cached) && cached.Sequence == sequence)
        {
            return cached.Tenant;
        }

        var found = await FindUncachedAsync(organizationId, projectId, database, cancellationToken).ConfigureAwait(false);
        if (found is not null)
        {
            if (_tenants.Count >= MaxCachedTenants)
            {
                _tenants.Clear();
            }

            _tenants[key] = (sequence, found);
        }

        return found;
    }

    private async ValueTask<TenantContext?> FindUncachedAsync(string organizationId, string projectId, string database, CancellationToken cancellationToken)
    {
        var tenant = await ReadAsync(transaction => FindDatabaseAsync(transaction, organizationId, projectId, database, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        return tenant is not null && await InitializeFollowerAsync(tenant, cancellationToken).ConfigureAwait(false) ? tenant : null;
    }

    private async ValueTask<bool> InitializeFollowerAsync(TenantContext tenant, CancellationToken cancellationToken)
    {
        // A follower serves a database the replicated catalog registers before any of its documents arrived: an empty
        // local file, which replication then fills like any other (docs/decisions/0009).
        // A deletion it applies meanwhile could miss the new file: one this call created is checked again, and deleted with it
        // once the catalog, at a version this build reads, no longer registers the database.
        if (options.CatalogPrimary is not null && await engine.OpenDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false) is null
            && await engine.CreateDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false)
            && await RegisteredAsync(tenant, cancellationToken).ConfigureAwait(false) is false)
        {
            if (await engine.OpenDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false) is { } created)
            {
                _ = await engine.DeleteDatabaseAsync(tenant, cancellationToken, incarnation: created.Incarnation).ConfigureAwait(false);
            }

            return false;
        }

        return true;
    }

    public async ValueTask<(TenantContext Database, bool Created)?> CreateDatabaseAsync(
        string organizationId, string projectId, string database, CancellationToken cancellationToken)
    {
        var added = await AddDatabaseAsync(
            async transaction => await OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false) is { } tenant
                ? tenant
                : ManagementError.NotFound,
            database, sourceId: null, branchedFrom: null, cancellationToken)
            .ConfigureAwait(false);
        if (added.Value is { } value)
        {
            return (value.Tenant, true);
        }

        // The name is taken: the database exists, unless it was deleted again meanwhile.
        return added.Error == ManagementError.NameTaken
            && await FindDatabaseAsync(organizationId, projectId, database, cancellationToken).ConfigureAwait(false) is { } existing
            ? (existing, false)
            : null;
    }

    public ValueTask<(string Name, DatabasePlacement Placement)?> DatabaseRecordAsync(TenantContext database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.ProjectId is null || database.DatabaseId is null
            ? ValueTask.FromResult<(string, DatabasePlacement)?>(null)
            : ReadCatalogAsync<(string, DatabasePlacement)?>(async transaction =>
                await GetAsync(transaction, Databases(database.ProjectId), database.DatabaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                    .ConfigureAwait(false) is { } record
                    ? (record.Name, record.Placement(options.Region))
                    : null,
                cancellationToken);
    }

    public ValueTask<TenantDatabasePage> ReadDatabaseRecordsAsync(string homeRegion, TenantContext? after, CancellationToken cancellationToken) =>
        ReadAsync(async transaction =>
        {
            // At most 100 identities, each with three UUIDs: under 22 KiB of character data. Storage scan pages are
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
                        var database = JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.ManagedDatabase)!;
                        if (database.Home(options.Region) == homeRegion)
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
        }, cancellationToken);

    public async ValueTask<DatabaseAdoption> AdoptDatabaseAsync(TenantContext database, string name, DatabasePlacement placement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(placement);
        if (database.ProjectId is null || database.DatabaseId is null || !DatabaseName.TryParse(name, out _))
        {
            return DatabaseAdoption.NotReplicated;
        }

        // Every database id this catalog ever registered keeps its entry, deleted ones as tombstones.
        if (await ReadAsync(async transaction => await transaction.GetVersionAsync(EngineManagedBackupService.BackedUpDatabases, database.DatabaseId, cancellationToken)
            .ConfigureAwait(false), cancellationToken).ConfigureAwait(false) is not null)
        {
            return DatabaseAdoption.Deleted;
        }

        async ValueTask<ManagementResult<TenantContext>> project(IStorageTransaction transaction)
        {
            return await OrganizationProjectAsync(transaction, database.OrganizationId, database.ProjectId!, cancellationToken).ConfigureAwait(false) is { } tenant
                ? tenant
                : ManagementError.NotFound;
        }

        ManagementError error;
        if (placement.Includes(options.Region))
        {
            error = (await AddDatabaseAsync(project, name, sourceId: null, branchedFrom: null, cancellationToken, id: database.DatabaseId, placement: placement)
                .ConfigureAwait(false)).Error;
        }
        else
        {
            // Placed elsewhere: only the record, no file and no backups, so requests here can be told where it is served.
            var record = new ManagedDatabase(
                database.DatabaseId, database.ProjectId, name, null, time.GetUtcNow(), placement.Home, placement.Replicas.Count == 0 ? null : [.. placement.Replicas], placement.Version);
            error = await WriteAsync(async transaction =>
            {
                var check = await CanAddDatabaseAsync(transaction, project, name, cancellationToken).ConfigureAwait(false);
                if (check.Value is { } tenant)
                {
                    await PutAsync(transaction, DatabaseNames(database.ProjectId), name, database.DatabaseId, Id, cancellationToken).ConfigureAwait(false);
                    await PutAsync(transaction, Databases(database.ProjectId), database.DatabaseId, record, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                        .ConfigureAwait(false);
                    await TouchOrganizationAsync(transaction, tenant.OrganizationId, cancellationToken).ConfigureAwait(false);
                }

                return check.Error;
            }, cancellationToken).ConfigureAwait(false);
        }

        return error == ManagementError.None ? DatabaseAdoption.Registered
            : error == ManagementError.NameTaken ? DatabaseAdoption.NameTaken
            : DatabaseAdoption.NotReplicated;
    }

    public async ValueTask<bool> UpdatePlacementAsync(TenantContext database, DatabasePlacement placement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(placement);
        if (database.ProjectId is null || database.DatabaseId is null)
        {
            return false;
        }

        return await WriteAsync(async transaction =>
        {
            if (await GetAsync(transaction, Databases(database.ProjectId), database.DatabaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                .ConfigureAwait(false) is not { } record || placement.Version <= record.PlacementVersion || record.Home(options.Region) == options.Region)
            {
                // The home region decides its databases' placement; nothing older replaces a newer one.
                return false;
            }

            await PutAsync(transaction, Databases(database.ProjectId), database.DatabaseId, record with
            {
                HomeRegion = placement.Home,
                ReplicaRegions = placement.Replicas.Count == 0 ? null : [.. placement.Replicas],
                PlacementVersion = placement.Version,
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
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> ForgetDatabaseAsync(TenantContext database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.ProjectId is null || database.DatabaseId is null)
        {
            return false;
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

        if (await ReadAsync(ElsewhereAsync, cancellationToken).ConfigureAwait(false) is not { } target)
        {
            return false;
        }

        return await DeleteRecordedAsync(target.Storage, async (transaction, token) =>
        {
            if (await ElsewhereAsync(transaction).ConfigureAwait(false) is not { } current)
            {
                return false;
            }

            _ = await UnregisterRecordAsync(transaction, current.Storage, current.Record, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ManagedDatabase?> FindDatabaseRecordAsync(string organizationId, string projectId, string name, CancellationToken cancellationToken)
    {
        var sequence = (await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)).CommitSequence;
        var key = (organizationId, projectId, name);
        if (_records.TryGetValue(key, out var cached) && cached.Sequence == sequence)
        {
            return cached.Record;
        }

        var record = await ReadAsync(async transaction =>
            await FindDatabaseAsync(transaction, organizationId, projectId, name, cancellationToken).ConfigureAwait(false) is { } tenant
                ? await GetAsync(transaction, Databases(projectId), tenant.DatabaseId!, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false)
                : null,
            cancellationToken).ConfigureAwait(false);
        if (_records.Count >= MaxCachedTenants)
        {
            _records.Clear();
        }

        _records[key] = (sequence, record);
        return record;
    }

    public ValueTask<PlanTier> PlanAsync(string organizationId, CancellationToken cancellationToken) =>
        ReadCatalogAsync(async transaction =>
            Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false))
                ? await PlanUncheckedAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false)
                : PlanTier.Hobby,
            cancellationToken);

    public ValueTask<bool?> RegisteredAsync(TenantContext database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.ProjectId is null || database.DatabaseId is null
            ? ValueTask.FromResult<bool?>(false)
            : ReadCatalogAsync<bool?>(async transaction =>
                !Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false))
                    ? null
                    : await ScopeExistsUncheckedAsync(transaction, database.OrganizationId, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false)
                        && (options.Wan is null
                            || (await GetAsync(transaction, Databases(database.ProjectId), database.DatabaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                                .ConfigureAwait(false))?.PlacedIn(options.Region) == true),
                cancellationToken);
    }

    public ValueTask<bool?> ReplicatesAsync(TenantContext database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.ProjectId is null || database.DatabaseId is null
            ? ValueTask.FromResult<bool?>(false)
            : ReadCatalogAsync<bool?>(async transaction =>
                !Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false))
                    ? null
                    : await ScopeExistsUncheckedAsync(transaction, database.OrganizationId, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false)
                        || await GetAsync(transaction, PendingDatabaseCleanup, database.DatabaseId, TerraBaseJsonContext.Default.TenantContext, cancellationToken)
                            .ConfigureAwait(false) is not null,
                cancellationToken);
    }

    // Whether this member reads registrations of a catalog at version: a primary every version it migrates from, which keep
    // the registration rows, as its startup audit does; a follower, which migrates nothing, only its own.
    private bool Interprets(string? version)
    {
        return options.CatalogPrimary is null
            ? version is null or MembershipCatalogVersion or "3" or IntentCatalogVersion or CatalogVersion
            : version == CatalogVersion;
    }

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
        && await GetAsync(transaction, DatabaseNames(projectId), database, Id, cancellationToken).ConfigureAwait(false) is { } databaseId
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
