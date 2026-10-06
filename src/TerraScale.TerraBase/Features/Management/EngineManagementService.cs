using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Features.Documents;
using TerraScale.TerraBase.Features.Management.Backups;
using TerraScale.TerraBase.Features.Replication;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>
/// Keeps the management catalog as documents in the system database (<see cref="IStorageEngine.OpenSystemDatabaseAsync"/>). Every
/// change runs in one write transaction, which the engine serializes, so membership checks, name checks
/// and writes are atomic. Names are unique per parent through <c>*_names</c> collections keyed by the
/// lowercased name; organization names are unique per creator, so they reveal nothing across tenants.
/// State other slices keep under a database or project
/// (<see cref="SystemCatalog.DatabaseState"/>, <see cref="SystemCatalog.ProjectState"/>) is deleted with it.
/// </summary>
/// <remarks>
/// Membership is stored twice: <c>organizations/{id}/members</c> (who belongs, with its
/// <see cref="OrganizationMember"/> role) and <c>users/{id}/organizations</c> (what a user lists). Every lookup
/// goes through <see cref="GetOrganizationAsync(IStorageTransaction, string, string, CancellationToken)"/> or
/// <c>AuthorizeAsync</c>, which answer non-members as if the organization did not exist.
/// <para>
/// The catalog is versioned by the <c>catalog/version</c> document, and migrated in one transaction before the
/// first operation of the process. Version 1 (no document) predates membership: its organizations have no
/// members and globally unique names, and every organization is assigned to
/// <see cref="TerraBaseOptions.ManagementLegacyOwner"/> as its owner; without that setting the API answers 503.
/// Version 2 stored members without roles; they all become owners. Version 3 upgrades to version 4, which
/// records pending database cleanup intents. Version 5 adds the viewer role, invitation history and token hashes,
/// organization and account versions, commit records and the global project index, which the upgrade from 4 fills.
/// Any other version is unsupported: the API answers 503 and the catalog is left unchanged.
/// </para>
/// <para>
/// Each organization is a tenant. Storage is addressed by a <see cref="TenantContext"/> resolved in the same transaction
/// as the membership check (or, for API keys, from the key's organization; see <see cref="ITenantDirectory"/>).
/// </para>
/// </remarks>
internal sealed partial class EngineManagementService(
    IStorageEngine engine, TimeProvider time, TerraBaseOptions options, ILogger<EngineManagementService> logger, DatabaseLifecycleCheckpoint checkpoint, IMeterFactory meters,
    Lazy<IDocumentService> documents)
    : IManagementService, IAccessService, IManagementCommits, ITenantDirectory, IHostedService, ICatalogGuard, ICatalogApplyObserver
{
    private const string Organizations = "organizations";
    private const string CurrentOrganizations = "current_organizations";
    private const string Catalog = "catalog";
    private const string CatalogVersionId = "version";

    // Markers of the one-time backfills (BackfillAsync), in the catalog collection.
    private const string DatabasesEnrolled = "backfill_databases_enrolled";
    private const string ProjectKeysListed = "backfill_project_keys_listed";
    private const string CatalogVersion = "5";

    // Version 4 added database cleanup intents; version 5 needs only the project index filled.
    private const string IntentCatalogVersion = "4";
    private const string PendingDatabaseCleanup = "pending_database_cleanup";

    // Version 2 added membership; its member documents hold only the user id.
    private const string MembershipCatalogVersion = "2";

    // Version 1 kept one global name index.
    private const string LegacyOrganizationNames = "organization_names";

    // Global: project id to its organization id, so a client-chosen project id is unique across organizations.
    private const string ProjectOrganizations = "project_organizations";

    // Catalog documents are small; this only bounds a page if one ever is not.
    private const long MaxPageBytes = 1024 * 1024;

    // Documents per page when deleting members or migrating the catalog.
    private const int MemberPage = 100;

    private static readonly JsonTypeInfo<string> Id = TerraBaseJsonContext.Default.String;

    private volatile bool _catalogCurrent;

    public ValueTask<ManagementResult<Organization>> CreateOrganizationAsync(string userId, string name, CancellationToken cancellationToken) =>
        CreateOrganizationAsync(userId, NewId(), name, cancellationToken);

    private async ValueTask<ManagementResult<Organization>> CreateOrganizationAsync(string userId, string id, string name, CancellationToken cancellationToken)
    {
        var organization = new Organization(id, name, userId, time.GetUtcNow());
        return await WriteAsync<ManagementResult<Organization>>(async transaction =>
        {
            if (await transaction.GetVersionAsync(Organizations, id, cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementError.IdTaken;
            }

            if (await GetAsync(transaction, OrganizationNames(userId), Key(name), Id, cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementError.NameTaken;
            }

            await TouchOrganizationAsync(transaction, organization.Id, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, OrganizationNames(userId), Key(name), organization.Id, Id, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Organizations, organization.Id, organization, TerraBaseJsonContext.Default.Organization, cancellationToken)
                .ConfigureAwait(false);
            await PutMemberAsync(transaction, organization.Id, new OrganizationMember(userId, OrganizationRole.Owner, organization.CreatedAt), cancellationToken)
                .ConfigureAwait(false);
            return organization;
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ManagementPage<Organization>> ListOrganizationsAsync(string userId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync(async transaction =>
        {
            var page = await transaction.ListAsync(UserOrganizations(userId), after, limit, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            var organizations = new List<Organization>(page.Documents.Count);
            foreach (var document in page.Documents)
            {
                // Written with the membership, removed with the organization: always present.
                organizations.Add(await GetAsync(transaction, Organizations, document.Id, TerraBaseJsonContext.Default.Organization, cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidOperationException("a membership names a missing organization"));
            }

            return new ManagementPage<Organization>(organizations, page.HasMore ? page.Documents[^1].Id : null);
        }, cancellationToken);

    public ValueTask<Organization?> GetOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        ReadAsync(transaction => GetOrganizationAsync(transaction, userId, organizationId, cancellationToken), cancellationToken);

    public async ValueTask<ManagementResult<Organization>> RenameOrganizationAsync(
        string userId, string organizationId, string name, CancellationToken cancellationToken) =>
        await WriteAsync<ManagementResult<Organization>>(async transaction =>
        {
            var actor = await AuthorizeAsync(transaction, userId, organizationId, OrganizationRole.Admin, cancellationToken).ConfigureAwait(false);
            if (actor.Error != ManagementError.None)
            {
                return actor.Error;
            }

            // Written with the membership, removed with the organization: always present.
            var organization = await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("a membership names a missing organization");

            if (!await RenameAsync(transaction, OrganizationNames(organization.CreatedBy), organization.Id, organization.Name, name, cancellationToken)
                .ConfigureAwait(false))
            {
                return ManagementError.NameTaken;
            }

            organization = organization with { Name = name };
            await PutAsync(transaction, Organizations, organization.Id, organization, TerraBaseJsonContext.Default.Organization, cancellationToken)
                .ConfigureAwait(false);
            await TouchOrganizationAsync(transaction, organization.Id, cancellationToken).ConfigureAwait(false);
            return organization;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ManagementResult<Organization>> SetPlanAsync(
        string userId, string organizationId, PlanTier plan, CancellationToken cancellationToken) =>
        await WriteAsync<ManagementResult<Organization>>(async transaction =>
        {
            // The plan is billing, which belongs to the owners.
            var actor = await AuthorizeAsync(transaction, userId, organizationId, OrganizationRole.Owner, cancellationToken).ConfigureAwait(false);
            if (actor.Error != ManagementError.None)
            {
                return actor.Error;
            }

            // Written with the membership, removed with the organization: always present.
            var organization = await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("a membership names a missing organization");
            organization = organization with { Plan = plan };
            await PutAsync(transaction, Organizations, organization.Id, organization, TerraBaseJsonContext.Default.Organization, cancellationToken)
                .ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "settings.plan_updated", null, PlanName(plan), cancellationToken).ConfigureAwait(false);
            return organization;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ManagementError> DeleteOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        await WriteAsync(async transaction =>
        {
            var actor = await AuthorizeAsync(transaction, userId, organizationId, OrganizationRole.Owner, cancellationToken).ConfigureAwait(false);
            if (actor.Error != ManagementError.None)
            {
                return actor.Error;
            }

            // Written with the membership, removed with the organization: always present.
            var organization = await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("a membership names a missing organization");

            if (await AnyAsync(transaction, Projects(organization.Id), cancellationToken).ConfigureAwait(false))
            {
                return ManagementError.NotEmpty;
            }

            DocumentPage members;
            do
            {
                // Each page starts from the beginning: the previous page's members are already deleted.
                members = await transaction.ListAsync(Members(organization.Id), afterId: null, MemberPage, MaxPageBytes, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var member in members.Documents)
                {
                    await TouchAccountAsync(transaction, member.Id, cancellationToken).ConfigureAwait(false);
                    await transaction.DeleteAsync(UserOrganizations(member.Id), organization.Id, cancellationToken).ConfigureAwait(false);
                    await transaction.DeleteAsync(Members(organization.Id), member.Id, cancellationToken).ConfigureAwait(false);
                }
            }
            while (members.HasMore);

            await DeleteAccessAsync(transaction, organization.Id, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(OrganizationNames(organization.CreatedBy), Key(organization.Name), cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Organizations, organization.Id, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(OrganizationVersions, organization.Id, cancellationToken).ConfigureAwait(false);
            return ManagementError.None;
        }, cancellationToken).ConfigureAwait(false);

    public ValueTask<Organization?> GetCurrentOrganizationAsync(string userId, CancellationToken cancellationToken) =>
        ReadAsync(async transaction =>
            await GetAsync(transaction, CurrentOrganizations, userId, Id, cancellationToken).ConfigureAwait(false) is { } organizationId
                ? await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false)
                : null,
            cancellationToken);

    public async ValueTask<ManagementResult<Organization>> SetCurrentOrganizationAsync(
        string userId, string organizationId, CancellationToken cancellationToken) =>
        await WriteAsync<ManagementResult<Organization>>(async transaction =>
        {
            if (await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is not { } organization)
            {
                return ManagementError.NotFound;
            }

            await PutAsync(transaction, CurrentOrganizations, userId, organization.Id, Id, cancellationToken).ConfigureAwait(false);
            await TouchAccountAsync(transaction, userId, cancellationToken).ConfigureAwait(false);
            return organization;
        }, cancellationToken).ConfigureAwait(false);

    public ValueTask<ManagementResult<Project>> CreateProjectAsync(
        string userId, string organizationId, string name, CancellationToken cancellationToken) =>
        CreateProjectAsync(userId, organizationId, NewId(), name, cancellationToken);

    private async ValueTask<ManagementResult<Project>> CreateProjectAsync(
        string userId, string organizationId, string id, string name, CancellationToken cancellationToken)
    {
        var project = new Project(id, organizationId, name, time.GetUtcNow());
        return await WriteAsync<ManagementResult<Project>>(async transaction =>
        {
            var writer = await AuthorizeAsync(transaction, userId, organizationId, OrganizationRole.Member, cancellationToken).ConfigureAwait(false);
            if (writer.Error != ManagementError.None)
            {
                return writer.Error;
            }

            if (await transaction.GetVersionAsync(ProjectOrganizations, id, cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementError.IdTaken;
            }

            if (await GetAsync(transaction, ProjectNames(organizationId), Key(name), Id, cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementError.NameTaken;
            }

            await PutAsync(transaction, ProjectOrganizations, project.Id, organizationId, Id, cancellationToken).ConfigureAwait(false);
            await TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, ProjectNames(organizationId), Key(name), project.Id, Id, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Projects(organizationId), project.Id, project, TerraBaseJsonContext.Default.Project, cancellationToken)
                .ConfigureAwait(false);
            return project;
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ManagementPage<Project>?> ListProjectsAsync(
        string userId, string organizationId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ManagementPage<Project>?>(async transaction =>
            await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is null
                ? null
                : await ListAsync(transaction, Projects(organizationId), after, limit, TerraBaseJsonContext.Default.Project, project => project.Id, cancellationToken)
                    .ConfigureAwait(false),
            cancellationToken);

    public ValueTask<Project?> GetProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        ReadAsync(transaction => GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken), cancellationToken);

    public async ValueTask<ManagementResult<Project>> RenameProjectAsync(
        string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken) =>
        await WriteAsync<ManagementResult<Project>>(async transaction =>
        {
            var writer = await AuthorizeAsync(transaction, userId, organizationId, OrganizationRole.Member, cancellationToken).ConfigureAwait(false);
            if (writer.Error != ManagementError.None)
            {
                return writer.Error;
            }

            if (await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is not { } project)
            {
                return ManagementError.NotFound;
            }

            if (!await RenameAsync(transaction, ProjectNames(organizationId), project.Id, project.Name, name, cancellationToken).ConfigureAwait(false))
            {
                return ManagementError.NameTaken;
            }

            await TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            project = project with { Name = name };
            await PutAsync(transaction, Projects(organizationId), project.Id, project, TerraBaseJsonContext.Default.Project, cancellationToken)
                .ConfigureAwait(false);
            return project;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ManagementError> DeleteProjectAsync(
        string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await WriteAsync(async transaction =>
        {
            var writer = await AuthorizeAsync(transaction, userId, organizationId, OrganizationRole.Member, cancellationToken).ConfigureAwait(false);
            if (writer.Error != ManagementError.None)
            {
                return writer.Error;
            }

            if (await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is not { } project)
            {
                return ManagementError.NotFound;
            }

            if (await AnyAsync(transaction, Databases(project.Id), cancellationToken).ConfigureAwait(false)
                || await AnyAsync(transaction, EngineApiKeyStore.ProjectKeys(project.Id), cancellationToken).ConfigureAwait(false))
            {
                return ManagementError.NotEmpty;
            }

            await TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(ProjectOrganizations, project.Id, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(ProjectNames(organizationId), Key(project.Name), cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Projects(organizationId), project.Id, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteCollectionsAsync(SystemCatalog.ProjectState(project.Id), cancellationToken).ConfigureAwait(false);
            return ManagementError.None;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ManagementResult<ManagedDatabase>> CreateDatabaseAsync(
        string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken)
    {
        var added = await AddDatabaseAsync(
            transaction => MemberProjectAsync(transaction, userId, organizationId, projectId, cancellationToken), name, sourceId: null, branchedFrom: null, cancellationToken)
            .ConfigureAwait(false);
        return added.Value is { } value ? value.Database : added.Error;
    }

    public ValueTask<ManagementPage<ManagedDatabase>?> ListDatabasesAsync(
        string userId, string organizationId, string projectId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ManagementPage<ManagedDatabase>?>(async transaction =>
            await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null
                ? null
                : await ListAsync(
                    transaction, Databases(projectId), after, limit, TerraBaseJsonContext.Default.ManagedDatabase, database => database.Id, cancellationToken)
                    .ConfigureAwait(false),
            cancellationToken);

    public ValueTask<ManagedDatabase?> GetDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        ReadAsync(transaction => GetDatabaseAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken), cancellationToken);

    public ValueTask<TenantContext?> GetDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        ReadAsync(transaction => MemberDatabaseTenantAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken), cancellationToken);

    public ValueTask<ManagementResult<TenantContext>> GetWritableDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        ReadAsync<ManagementResult<TenantContext>>(async transaction =>
        {
            var writer = await WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            return writer != ManagementError.None ? writer
                : await MemberDatabaseTenantAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is { } tenant
                    ? tenant
                    : ManagementError.NotFound;
        }, cancellationToken);

    public ValueTask<ManagementResult<Project>> GetWritableProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        ReadAsync<ManagementResult<Project>>(async transaction =>
        {
            var writer = await WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            return writer != ManagementError.None ? writer
                : await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is { } project
                    ? project
                    : ManagementError.NotFound;
        }, cancellationToken);

    /// <summary>
    /// Whether the user may change the organization's projects, databases and their tooling state: NotFound for a non-member,
    /// Forbidden for a viewer, inside the caller's transaction.
    /// </summary>
    internal static async ValueTask<ManagementError> WritableAsync(
        IStorageTransaction transaction, string userId, string organizationId, CancellationToken cancellationToken) =>
        (await AuthorizeAsync(transaction, userId, organizationId, OrganizationRole.Member, cancellationToken).ConfigureAwait(false)).Error;

    public async ValueTask<ManagementError> DeleteDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken)
    {
        var target = await ReadAsync<ManagementResult<TenantContext>>(async transaction =>
        {
            var project = await MemberProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            return project.Value is not { } tenant ? project.Error
                : await GetAsync(transaction, Databases(projectId), databaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is { } database
                    ? tenant with { DatabaseId = database.Id }
                    : ManagementError.NotFound;
        }, cancellationToken).ConfigureAwait(false);
        if (target.Value is not { } storage)
        {
            return target.Error;
        }

        var recording = false;
        bool recorded;
        try
        {
            recorded = await engine.RecordDatabaseDeleteIntentAsync(storage, async token =>
            {
                recording = true;
                checkpoint("delete.recording", storage);
                return await WriteAsync(async transaction =>
                    (await UnregisterDatabaseAsync(transaction, userId, organizationId, projectId, databaseId, token).ConfigureAwait(false)).Error == ManagementError.None,
                    token).ConfigureAwait(false);
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
            return ManagementError.NotFound;
        }

        checkpoint("delete.intent", storage);
        // The committed intent survives failure or cancellation of the file operation.
        await FinishDatabaseCleanupAsync(storage, CancellationToken.None).ConfigureAwait(false);
        checkpoint("delete.finalized", storage);
        return ManagementError.None;
    }

    // Removes a database from the catalog and records its cleanup intent, in the caller's transaction; the files go
    // after it commits (FinishDatabaseCleanupAsync), or at the next start.
    private static async ValueTask<ManagementResult<TenantContext>> UnregisterDatabaseAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken)
    {
        var project = await MemberProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
        if (project.Value is not { } tenant)
        {
            return project.Error;
        }

        if (await GetAsync(transaction, Databases(projectId), databaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not { } database)
        {
            return ManagementError.NotFound;
        }

        var storage = tenant with { DatabaseId = database.Id };
        await PutAsync(transaction, PendingDatabaseCleanup, database.Id, storage, TerraBaseJsonContext.Default.TenantContext, cancellationToken).ConfigureAwait(false);
        await transaction.DeleteAsync(DatabaseNames(projectId), database.Name, cancellationToken).ConfigureAwait(false);
        await transaction.DeleteAsync(Databases(projectId), database.Id, cancellationToken).ConfigureAwait(false);
        // The database's backups expire through the storage sweep from this commit on, and its tooling state
        // (backup schedule, usage and query field counts) goes with it.
        await StorageSweep.AddAsync(transaction, new SweepEntry(StorageSweep.DatabaseBackups, organizationId, database.Id), cancellationToken).ConfigureAwait(false);
        await transaction.DeleteAsync(EngineManagedBackupService.BackedUpDatabases, database.Id, cancellationToken).ConfigureAwait(false);
        await transaction.DeleteCollectionsAsync(SystemCatalog.DatabaseState(database.Id), cancellationToken).ConfigureAwait(false);
        await TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
        return storage;
    }

    public async ValueTask<ManagementResult<ManagedDatabase>> BranchDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, string name, CancellationToken cancellationToken)
    {
        if (await GetDatabaseAsync(userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is not { } source)
        {
            return ManagementError.NotFound;
        }

        var added = await AddDatabaseAsync(
            transaction => MemberProjectAsync(transaction, userId, organizationId, projectId, cancellationToken), name, source.Id, source.Id, cancellationToken)
            .ConfigureAwait(false);
        return added.Value is { } value ? value.Database : added.Error;
    }

    public async ValueTask<ManagementResult<ManagedDatabase>> CopyDatabaseAsync(
        string userId, string organizationId, string projectId, string sourceId, string name, string? branchedFrom, CancellationToken cancellationToken)
    {
        var added = await AddDatabaseAsync(
            transaction => MemberProjectAsync(transaction, userId, organizationId, projectId, cancellationToken), name, sourceId, branchedFrom, cancellationToken)
            .ConfigureAwait(false);
        return added.Value is { } value ? value.Database : added.Error;
    }

    // Records cleanup ownership before creating (or copying) storage, then registers the database and
    // clears the intent in one transaction. project resolves the tenant of the project to add to (or why there is
    // none) inside each transaction. A copy reads the engine database sourceId of that project (a database or a
    // backup snapshot).
    private async ValueTask<ManagementResult<AddedDatabase>> AddDatabaseAsync(
        Func<IStorageTransaction, ValueTask<ManagementResult<TenantContext>>> project, string name, string? sourceId, string? branchedFrom, CancellationToken cancellationToken)
    {
        // Checked again when registering; this avoids creating storage for a request that cannot succeed.
        var precheck = await ReadAsync(transaction => CanAddDatabaseAsync(transaction, project, name, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        if (precheck.Value is not { } tenant)
        {
            return precheck.Error;
        }

        var database = new ManagedDatabase(NewId(), tenant.ProjectId!, name, branchedFrom, time.GetUtcNow());
        var storage = tenant with { DatabaseId = database.Id };
        if (!await PrepareDatabaseAsync(storage, sourceId is null ? null : tenant with { DatabaseId = sourceId }, cancellationToken).ConfigureAwait(false))
        {
            return ManagementError.NotFound;
        }

        ManagementError error;
        try
        {
            error = await WriteAsync(transaction => RegisterDatabaseAsync(transaction, project, database, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (error == ManagementError.None)
            {
                checkpoint("create.committed", storage);
            }
        }
        catch
        {
            await AbandonDatabaseAsync(storage).ConfigureAwait(false);
            throw;
        }

        if (error != ManagementError.None)
        {
            await FinishDatabaseCleanupAsync(storage, CancellationToken.None).ConfigureAwait(false);
            return error;
        }

        await engine.CompleteDatabaseIntentAsync(storage, CancellationToken.None).ConfigureAwait(false);
        checkpoint("create.registered", storage);
        return new AddedDatabase(database, storage);
    }

    // Creates (or copies source into) storage under a committed cleanup intent, which stays until registration or
    // cleanup clears it. False when the branch source is missing, leaving nothing behind.
    private async ValueTask<bool> PrepareDatabaseAsync(TenantContext storage, TenantContext? source, CancellationToken cancellationToken)
    {
        checkpoint("create.prepare", storage);
        var recording = false;
        var publishing = false;
        bool created;
        try
        {
            created = await engine.CreateDatabaseWithIntentAsync(storage, source, async token =>
            {
                recording = true;
                _ = await WriteCatalogAsync(async transaction =>
                {
                    await PutAsync(transaction, PendingDatabaseCleanup, storage.DatabaseId!, storage, TerraBaseJsonContext.Default.TenantContext, token).ConfigureAwait(false);
                    return 0;
                }, token).ConfigureAwait(false);
                checkpoint("create.intent", storage);
                publishing = true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A rejected destination never acquired ownership. A stop in the intent callback leaves recovery work.
            if (publishing)
            {
                await FinishDatabaseCleanupAsync(storage, CancellationToken.None).ConfigureAwait(false);
            }
            else if (recording)
            {
                await ReleaseUnrecordedIntentAsync(storage).ConfigureAwait(false);
            }

            throw;
        }

        if (!created)
        {
            // A missing branch source made no target.
            await ClearDatabaseCleanupAsync(storage, CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        checkpoint("create.files", storage);
        return true;
    }

    // Registers prepared storage and clears its intent in the caller's transaction, when the project still allows it.
    private static async ValueTask<ManagementError> RegisterDatabaseAsync(
        IStorageTransaction transaction, Func<IStorageTransaction, ValueTask<ManagementResult<TenantContext>>> project, ManagedDatabase database,
        CancellationToken cancellationToken)
    {
        var check = await CanAddDatabaseAsync(transaction, project, database.Name, cancellationToken).ConfigureAwait(false);
        if (check.Value is { } tenant)
        {
            await PutAsync(transaction, DatabaseNames(database.ProjectId), database.Name, database.Id, Id, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Databases(database.ProjectId), database.Id, database, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                .ConfigureAwait(false);
            // Every database is backed up under its effective policy, the default until one is saved.
            await PutAsync(transaction, EngineManagedBackupService.BackedUpDatabases, database.Id, DatabaseOwner.Of(tenant with { DatabaseId = database.Id }),
                TerraBaseJsonContext.Default.DatabaseOwner, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(PendingDatabaseCleanup, database.Id, cancellationToken).ConfigureAwait(false);
            await TouchOrganizationAsync(transaction, tenant.OrganizationId, cancellationToken).ConfigureAwait(false);
        }

        return check.Error;
    }

    // Resolves prepared storage after an error around its registration: the commit can succeed before a later
    // transaction or disposal error, so both catalog facts are confirmed in one snapshot.
    private async ValueTask AbandonDatabaseAsync(TenantContext storage)
    {
        var registered = await ReadCatalogAsync(async transaction =>
            await transaction.GetAsync(Databases(storage.ProjectId!), storage.DatabaseId!, CancellationToken.None).ConfigureAwait(false) is not null
            && await transaction.GetAsync(PendingDatabaseCleanup, storage.DatabaseId!, CancellationToken.None).ConfigureAwait(false) is null,
            CancellationToken.None).ConfigureAwait(false);
        if (registered)
        {
            await engine.CompleteDatabaseIntentAsync(storage, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await FinishDatabaseCleanupAsync(storage, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // The project's tenant when the project exists and the name is free in it.
    private static async ValueTask<ManagementResult<TenantContext>> CanAddDatabaseAsync(
        IStorageTransaction transaction, Func<IStorageTransaction, ValueTask<ManagementResult<TenantContext>>> project, string name, CancellationToken cancellationToken)
    {
        var resolved = await project(transaction).ConfigureAwait(false);
        if (resolved.Value is not { } tenant)
        {
            return resolved.Error;
        }

        return await GetAsync(transaction, DatabaseNames(tenant.ProjectId!), name, Id, cancellationToken).ConfigureAwait(false) is null
            ? tenant
            : ManagementError.NameTaken;
    }

    // A project of the organization as a tenant: null when there is no organization or the project is not one of its projects.
    private static async ValueTask<TenantContext?> ProjectTenantAsync(
        IStorageTransaction transaction, Organization? organization, string projectId, CancellationToken cancellationToken) =>
        organization is not null
        && await GetAsync(transaction, Projects(organization.Id), projectId, TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false) is not null
            ? new TenantContext(organization.Id, organization.Plan, projectId)
            : null;

    // The tenant of a project whose databases the user may change: NotFound for a non-member or an unknown project,
    // Forbidden for a viewer.
    private static async ValueTask<ManagementResult<TenantContext>> MemberProjectAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, CancellationToken cancellationToken)
    {
        var writer = await AuthorizeAsync(transaction, userId, organizationId, OrganizationRole.Member, cancellationToken).ConfigureAwait(false);
        if (writer.Error != ManagementError.None)
        {
            return writer.Error;
        }

        return await OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false) is { } tenant
            ? tenant
            : ManagementError.NotFound;
    }

    // The project's database as a tenant: null when the project is not found or has no such database.
    private static async ValueTask<TenantContext?> DatabaseTenantAsync(
        IStorageTransaction transaction, TenantContext? project, string databaseId, CancellationToken cancellationToken) =>
        project is not null
        && await GetAsync(transaction, Databases(project.ProjectId!), databaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
            .ConfigureAwait(false) is not null
            ? project with { DatabaseId = databaseId }
            : null;

    /// <summary>The tenant of a database the user reaches through membership (any role), inside a catalog transaction.</summary>
    internal static async ValueTask<TenantContext?> MemberDatabaseTenantAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        await DatabaseTenantAsync(
            transaction,
            await ProjectTenantAsync(transaction, await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false), projectId, cancellationToken)
                .ConfigureAwait(false),
            databaseId, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// The tenant of a database inside a catalog transaction without a membership check, for background work that acts for
    /// no user: the organization (with its plan), the project among its projects and the database among the project's.
    /// </summary>
    internal static async ValueTask<TenantContext?> DatabaseTenantUncheckedAsync(
        IStorageTransaction transaction, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        await DatabaseTenantAsync(
            transaction, await OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false), databaseId, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>A registered database inside a catalog transaction, without a membership check; null once it is deleted.</summary>
    internal static ValueTask<ManagedDatabase?> DatabaseUncheckedAsync(
        IStorageTransaction transaction, string projectId, string databaseId, CancellationToken cancellationToken) =>
        GetAsync(transaction, Databases(projectId), databaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken);

    /// <summary>Every registered database, for one-time catalog backfills; reads the whole catalog.</summary>
    internal static async ValueTask<List<DatabaseOwner>> ListDatabasesUncheckedAsync(IStorageTransaction transaction, CancellationToken cancellationToken)
    {
        var owners = new List<DatabaseOwner>();
        foreach (var organization in await ListAllAsync(transaction, Organizations, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false))
        {
            foreach (var project in await ListAllAsync(transaction, Projects(organization.Id), TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false))
            {
                foreach (var database in await ListAllAsync(transaction, Databases(project.Id), TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                    .ConfigureAwait(false))
                {
                    owners.Add(new DatabaseOwner(organization.Id, project.Id, database.Id));
                }
            }
        }

        return owners;
    }

    /// <summary>The plan of an organization inside a catalog transaction, or the default plan once it is deleted.</summary>
    internal static async ValueTask<PlanTier> PlanUncheckedAsync(IStorageTransaction transaction, string organizationId, CancellationToken cancellationToken) =>
        (await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false))?.Plan
        ?? PlanTier.Hobby;

    // The single authorization point: non-members get null, exactly like a missing organization.
    private static async ValueTask<Organization?> GetOrganizationAsync(
        IStorageTransaction transaction, string userId, string organizationId, CancellationToken cancellationToken) =>
        await GetMemberAsync(transaction, organizationId, userId, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false);

    // A project is looked up under its organization, so a project id under another organization is not found.
    internal static async ValueTask<Project?> GetProjectAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await GetAsync(transaction, Projects(organizationId), projectId, TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false);

    /// <summary>The database inside a catalog transaction, for slices that register state atomically with its existence.</summary>
    internal static async ValueTask<ManagedDatabase?> GetDatabaseAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await GetAsync(transaction, Databases(projectId), databaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                .ConfigureAwait(false);

    // Moves a name claim from oldName to newName; false when another resource holds newName.
    private static async ValueTask<bool> RenameAsync(
        IStorageTransaction transaction, string names, string id, string oldName, string newName, CancellationToken cancellationToken)
    {
        var holder = await GetAsync(transaction, names, Key(newName), Id, cancellationToken).ConfigureAwait(false);
        if (holder is not null && holder != id)
        {
            return false;
        }

        await transaction.DeleteAsync(names, Key(oldName), cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, names, Key(newName), id, Id, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async ValueTask<bool> AnyAsync(IStorageTransaction transaction, string collection, CancellationToken cancellationToken) =>
        (await transaction.ListAsync(collection, afterId: null, limit: 1, MaxPageBytes, cancellationToken).ConfigureAwait(false)).Documents.Count > 0;

    private static async ValueTask<ManagementPage<T>> ListAsync<T>(
        IStorageTransaction transaction, string collection, string? after, int limit, JsonTypeInfo<T> type, Func<T, string> id, CancellationToken cancellationToken)
    {
        var page = await transaction.ListAsync(collection, after, limit, MaxPageBytes, cancellationToken).ConfigureAwait(false);
        var items = page.Documents.Select(document => JsonSerializer.Deserialize(document.Json.Span, type)!).ToList();
        return new ManagementPage<T>(items, page.HasMore ? id(items[^1]) : null);
    }

    private static async ValueTask<T?> GetAsync<T>(
        IStorageTransaction transaction, string collection, string id, JsonTypeInfo<T> type, CancellationToken cancellationToken)
        where T : class
    {
        var document = await transaction.GetAsync(collection, id, cancellationToken).ConfigureAwait(false);
        return document is null ? null : JsonSerializer.Deserialize(document.Json.Span, type);
    }

    private static async ValueTask PutAsync<T>(
        IStorageTransaction transaction, string collection, string id, T value, JsonTypeInfo<T> type, CancellationToken cancellationToken) =>
        await transaction.PutAsync(collection, id, JsonSerializer.SerializeToUtf8Bytes(value, type), cancellationToken).ConfigureAwait(false);

    // Reads see a snapshot; inside a commit, the commit's transaction.
    private async ValueTask<T> ReadAsync<T>(Func<IStorageTransaction, ValueTask<T>> read, CancellationToken cancellationToken)
    {
        if (CommitTransaction.Value is { } commit)
        {
            return await read(commit).ConfigureAwait(false);
        }

        await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCatalogAsync(async transaction =>
        {
            await CheckReadAsync(transaction, cancellationToken).ConfigureAwait(false);
            return await read(transaction).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CheckReadAsync(IStorageTransaction transaction, CancellationToken cancellationToken)
    {
        if (options.CatalogPrimary is not null
            && await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false) != CatalogVersion)
        {
            throw new BadHttpRequestException(
                "the replicated management catalog is not at a version this build supports", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private async ValueTask<T> ReadCatalogAsync<T>(Func<IStorageTransaction, ValueTask<T>> read, CancellationToken cancellationToken)
    {
        RequireNoCommit();
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await read(transaction).ConfigureAwait(false);
        }
    }

    // Commits whatever the callback wrote; a failure result simply wrote nothing. Inside a commit it writes into the
    // commit's transaction, which commits (or rolls back) with every other operation of the commit.
    private async ValueTask<T> WriteAsync<T>(Func<IStorageTransaction, ValueTask<T>> write, CancellationToken cancellationToken)
    {
        if (CommitTransaction.Value is { } commit)
        {
            return await write(commit).ConfigureAwait(false);
        }

        await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
        return await WriteCatalogAsync(write, cancellationToken).ConfigureAwait(false);
    }

    // Commits what the callback wrote, unless keep rejects its result: then the transaction rolls back on disposal.
    private async ValueTask<T> WriteCatalogAsync<T>(Func<IStorageTransaction, ValueTask<T>> write, CancellationToken cancellationToken, Func<T, bool>? keep = null)
    {
        RequireNoCommit();
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
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

    // Brings the catalog to the current version once per process. Concurrent first calls serialize on the
    // write transaction; the later ones find the version document and change nothing.
    public async ValueTask EnsureCatalogAsync(CancellationToken cancellationToken)
    {
        if (_catalogCurrent)
        {
            return;
        }

        if (options.CatalogPrimary is not null)
        {
            // A follower migrates nothing: the primary's migration and backfills replicate. Each read checks the version in its
            // own transaction (CheckReadAsync); a write is refused before it reads anything.
            return;
        }

        var migrated = await WriteCatalogAsync(async transaction =>
        {
            var version = await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false);
            if (version == CatalogVersion)
            {
                return 0;
            }

            if (version is "3" or IntentCatalogVersion)
            {
                await IndexProjectsAsync(transaction, cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, Catalog, CatalogVersionId, CatalogVersion, Id, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            if (version == MembershipCatalogVersion)
            {
                // Version 2 could only make an organization's creator (or the legacy owner) a member.
                foreach (var organization in await ListAllAsync(transaction, Organizations, TerraBaseJsonContext.Default.Organization, cancellationToken)
                    .ConfigureAwait(false))
                {
                    foreach (var member in await ListAllAsync(transaction, Members(organization.Id), Id, cancellationToken).ConfigureAwait(false))
                    {
                        await PutMemberAsync(transaction, organization.Id, new OrganizationMember(member, OrganizationRole.Owner, organization.CreatedAt), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                await IndexProjectsAsync(transaction, cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, Catalog, CatalogVersionId, CatalogVersion, Id, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            if (version is not null)
            {
                // Never migrate, or downgrade, a catalog this build does not know.
                LogUnsupportedCatalog(logger);
                throw new BadHttpRequestException(
                    "the management catalog has a version this build does not support", StatusCodes.Status503ServiceUnavailable);
            }

            var legacy = await ListAllAsync(transaction, Organizations, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false);
            if (legacy.Count > 0 && options.ManagementLegacyOwner is not { } owner)
            {
                LogLegacyCatalog(logger, legacy.Count);
                throw new BadHttpRequestException(
                    "the management catalog predates membership; set management-legacy-owner to assign its organizations",
                    StatusCodes.Status503ServiceUnavailable);
            }

            foreach (var organization in legacy)
            {
                // Version 1 names were globally unique, so they cannot collide in the owner's new index.
                var owned = organization with { CreatedBy = options.ManagementLegacyOwner! };
                await transaction.DeleteAsync(LegacyOrganizationNames, Key(owned.Name), cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, OrganizationNames(owned.CreatedBy), Key(owned.Name), owned.Id, Id, cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, Organizations, owned.Id, owned, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false);
                await PutMemberAsync(transaction, owned.Id, new OrganizationMember(owned.CreatedBy, OrganizationRole.Owner, owned.CreatedAt), cancellationToken)
                    .ConfigureAwait(false);
            }

            await IndexProjectsAsync(transaction, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Catalog, CatalogVersionId, CatalogVersion, Id, cancellationToken).ConfigureAwait(false);
            return legacy.Count;
        }, cancellationToken).ConfigureAwait(false);
        if (migrated > 0)
        {
            LogLegacyCatalogMigrated(logger, migrated);
        }

        await BackfillAsync(cancellationToken).ConfigureAwait(false);
        _catalogCurrent = true;
    }

    // State that builds before this one did not keep, filled in once per catalog; each step records its marker in the
    // same write. ponytail: each step is one write over every database or key, once; page them if catalogs that old grow large.
    private ValueTask<int> BackfillAsync(CancellationToken cancellationToken) =>
        WriteCatalogAsync(async transaction =>
        {
            // Every database is backed up under its effective policy; registration enrolls it with the scheduler.
            if (await GetAsync(transaction, Catalog, DatabasesEnrolled, Id, cancellationToken).ConfigureAwait(false) is null)
            {
                foreach (var owner in await ListDatabasesUncheckedAsync(transaction, cancellationToken).ConfigureAwait(false))
                {
                    await PutAsync(transaction, EngineManagedBackupService.BackedUpDatabases, owner.DatabaseId, owner, TerraBaseJsonContext.Default.DatabaseOwner, cancellationToken)
                        .ConfigureAwait(false);
                }

                await PutAsync(transaction, Catalog, DatabasesEnrolled, DatabasesEnrolled, Id, cancellationToken).ConfigureAwait(false);
            }

            // Every project key is listed with its project, whichever route issued it.
            if (await GetAsync(transaction, Catalog, ProjectKeysListed, Id, cancellationToken).ConfigureAwait(false) is null)
            {
                await EngineApiKeyStore.ListUnlistedProjectKeysAsync(transaction, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, Catalog, ProjectKeysListed, ProjectKeysListed, Id, cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }, cancellationToken);

    // Fills the global project index from every organization's projects (catalogs before version 5 have none).
    private static async ValueTask IndexProjectsAsync(IStorageTransaction transaction, CancellationToken cancellationToken)
    {
        foreach (var organization in await ListAllAsync(transaction, Organizations, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false))
        {
            foreach (var project in await ListAllAsync(transaction, Projects(organization.Id), TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false))
            {
                await PutAsync(transaction, ProjectOrganizations, project.Id, organization.Id, Id, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask<List<T>> ListAllAsync<T>(
        IStorageTransaction transaction, string collection, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? after = null;
        DocumentPage page;
        do
        {
            page = await transaction.ListAsync(collection, after, MemberPage, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            items.AddRange(page.Documents.Select(document => JsonSerializer.Deserialize(document.Json.Span, type)!));
            after = page.Documents.Count > 0 ? page.Documents[^1].Id : after;
        }
        while (page.HasMore);

        return items;
    }

    // Counts only: organization ids and user ids stay out of the logs.
    [LoggerMessage(Level = LogLevel.Error, Message = "The management catalog holds {Count} organizations from before membership; set management-legacy-owner to migrate it")]
    private static partial void LogLegacyCatalog(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The management catalog has a version this build does not support; it was left unchanged")]
    private static partial void LogUnsupportedCatalog(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrated the management catalog: assigned {Count} organizations to management-legacy-owner")]
    private static partial void LogLegacyCatalogMigrated(ILogger logger, int count);

    private string NewId() => Guid.CreateVersion7(time.GetUtcNow()).ToString();

    private static string Key(string name) => name.ToLowerInvariant();

    private static string OrganizationNames(string userId) => $"users/{userId}/organization_names";

    private static string UserOrganizations(string userId) => $"users/{userId}/organizations";

    private static string Members(string organizationId) => $"organizations/{organizationId}/members";

    private static string Projects(string organizationId) => $"organizations/{organizationId}/projects";

    private static string ProjectNames(string organizationId) => $"organizations/{organizationId}/project_names";

    private static string Databases(string projectId) => $"projects/{projectId}/databases";

    private static string DatabaseNames(string projectId) => $"projects/{projectId}/database_names";

    // A database just added to the catalog, with the tenant its storage is addressed by.
    private sealed record AddedDatabase(ManagedDatabase Database, TenantContext Tenant);
}

/// <summary>Where a database is registered: its organization (tenant), project and id.</summary>
/// <param name="OrganizationId">Owning organization.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="DatabaseId">The database.</param>
internal sealed record DatabaseOwner(string OrganizationId, string ProjectId, string DatabaseId)
{
    /// <summary>The database a tenant context addresses.</summary>
    public static DatabaseOwner Of(TenantContext database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new(database.OrganizationId, database.ProjectId!, database.DatabaseId!);
    }
}
