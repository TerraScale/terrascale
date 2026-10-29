using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Documents;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Management.Queries;
using TerraBase.Features.Memory;
using TerraBase.Features.Platform;
using TerraBase.Features.Qos;
using TerraBase.Features.Catalog;
using TerraBase.Features.UserAuth;
using TerraBase.Features.Webhooks;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService(
    IStorageEngine engine, IOrphanDatabaseFiles orphanFiles, TimeProvider time, TerraBaseOptions options, ILogger<EngineManagementService> logger, DatabaseLifecycleCheckpoint checkpoint, IMeterFactory meters,
    Lazy<IDocumentService> documents, Lazy<IQueryShapeTracker> shapes, IMemoryBudget memory, IProjectAuthorityVerifier projectAuthority, Lazy<IStorageQuota> quota,
    Lazy<ICatalogGate> catalogGate, Lazy<IWebhookErasure> webhookErasure, DatabaseAllowance databaseAllowance,
    Lazy<IEscrowPeers> escrowPeers)
    : IManagementService, IAccessService, IManagementCommits, ITenantDirectory, IHostedService, ICatalogGuard, IDatabaseFileCatalog
{
    private const string Organizations = "organizations";
    private const string CurrentOrganizations = "current_organizations";
    private const string Catalog = ManagementCatalog.Collection;
    private const string CatalogVersionId = ManagementCatalog.VersionId;

    private const string CatalogVersion = ManagementCatalog.Version;
    private const string PendingDatabaseCleanup = "pending_database_cleanup";

    private const string ProjectOrganizations = "project_organizations";

    private const long MaxPageBytes = 1024 * 1024;

    private const int MemberPage = 100;

    private static readonly JsonTypeInfo<string> Id = TerraBaseJsonContext.Default.String;

    private volatile bool _catalogCurrent;

    public ValueTask<CreateOrganizationResult> CreateOrganizationAsync(string userId, string name, CancellationToken cancellationToken) =>
        CreateOrganizationAsync(userId, NewId(), name, cancellationToken);

    private async ValueTask<CreateOrganizationResult> CreateOrganizationAsync(string userId, string id, string name, CancellationToken cancellationToken)
    {
        var organization = new Organization(id, name, userId, time.GetUtcNow());
        return await WriteAsync<CreateOrganizationResult>(async transaction =>
        {
            if (await transaction.GetVersionAsync(Organizations, id, cancellationToken).ConfigureAwait(false) is not null)
            {
                if (await GetAsync(transaction, Organizations, id, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false) is { } known
                    && known.Name == name && known.CreatedBy == userId
                    && await GetMemberAsync(transaction, id, userId, cancellationToken).ConfigureAwait(false) is { Role: OrganizationRole.Owner })
                {
                    return known;
                }

                return ManagementErrors.IdTaken;
            }

            if (await GetAsync(transaction, OrganizationNames(userId), Key(name), Id, cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementErrors.NameTaken;
            }

            await TouchOrganizationAsync(transaction, organization.Id, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, OrganizationNames(userId), Key(name), organization.Id, Id, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Organizations, organization.Id, organization, TerraBaseJsonContext.Default.Organization, cancellationToken)
                .ConfigureAwait(false);
            await PutMemberAsync(transaction, organization.Id, new OrganizationMember(userId, OrganizationRole.Owner, organization.CreatedAt), cancellationToken)
                .ConfigureAwait(false);
            return organization;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ListOrganizationsResult> ListOrganizationsAsync(string userId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ListOrganizationsResult>(async transaction =>
        {
            var page = await transaction.ListAsync(UserOrganizations(userId), after, limit, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            var organizations = new List<Organization>(page.Documents.Count);
            foreach (var document in page.Documents)
            {
                organizations.Add(await GetAsync(transaction, Organizations, document.Id, TerraBaseJsonContext.Default.Organization, cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidOperationException("a membership names a missing organization"));
            }

            return new ManagementPage<Organization>(organizations, page.HasMore ? page.Documents[^1].Id : null);
        }, static error => error, cancellationToken);

    public ValueTask<GetOrganizationResult> GetOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        ReadAsync<GetOrganizationResult>(async transaction =>
            await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is { } organization
                ? organization
                : ManagementErrors.NotFound,
            static error => error, cancellationToken);

    public async ValueTask<RenameOrganizationResult> RenameOrganizationAsync(
        string userId, string organizationId, string name, CancellationToken cancellationToken) =>
        await WriteAsync<RenameOrganizationResult>(async transaction =>
        {
            var access = await AuthorizeAsync(transaction, userId, organizationId, OrganizationRole.Admin, cancellationToken).ConfigureAwait(false);
            if (access is not OrganizationMember)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    OrganizationMember => throw new UnreachableException(),
                };
            }

            var organization = await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("a membership names a missing organization");

            if (!await RenameAsync(transaction, OrganizationNames(organization.CreatedBy), organization.Id, organization.Name, name, cancellationToken)
                .ConfigureAwait(false))
            {
                return ManagementErrors.NameTaken;
            }

            organization = organization with { Name = name };
            await PutAsync(transaction, Organizations, organization.Id, organization, TerraBaseJsonContext.Default.Organization, cancellationToken)
                .ConfigureAwait(false);
            await TouchOrganizationAsync(transaction, organization.Id, cancellationToken).ConfigureAwait(false);
            return organization;
        }, static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<SetPlanResult> SetPlanAsync(
        string userId, string organizationId, PlanTier plan, CancellationToken cancellationToken) =>
        await WriteAsync<SetPlanResult>(async transaction =>
        {
            var access = await AuthorizeAsync(transaction, userId, organizationId, OrganizationRole.Owner, cancellationToken).ConfigureAwait(false);
            if (access is not OrganizationMember)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    OrganizationMember => throw new UnreachableException(),
                };
            }

            var organization = await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("a membership names a missing organization");
            organization = organization with { Plan = plan };
            await PutAsync(transaction, Organizations, organization.Id, organization, TerraBaseJsonContext.Default.Organization, cancellationToken)
                .ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "settings.plan_updated", null, PlanName(plan), cancellationToken).ConfigureAwait(false);
            return organization;
        }, static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<DeleteOrganizationResult> DeleteOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        await WriteAsync<DeleteOrganizationResult>(async transaction =>
        {
            var access = await AuthorizeAsync(transaction, userId, organizationId, OrganizationRole.Owner, cancellationToken).ConfigureAwait(false);
            if (access is not OrganizationMember)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    OrganizationMember => throw new UnreachableException(),
                };
            }

            var organization = await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("a membership names a missing organization");

            if (await AnyAsync(transaction, Projects(organization.Id), cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.NotEmpty;
            }

            DocumentPage members;
            do
            {
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
            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);

    public ValueTask<GetCurrentOrganizationResult> GetCurrentOrganizationAsync(string userId, CancellationToken cancellationToken) =>
        ReadAsync<GetCurrentOrganizationResult>(async transaction =>
            await GetAsync(transaction, CurrentOrganizations, userId, Id, cancellationToken).ConfigureAwait(false) is { } organizationId
            && await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is { } organization
                ? organization
                : ManagementErrors.NotFound,
            static error => error, cancellationToken);

    public async ValueTask<SetCurrentOrganizationResult> SetCurrentOrganizationAsync(
        string userId, string organizationId, CancellationToken cancellationToken) =>
        await WriteAsync<SetCurrentOrganizationResult>(async transaction =>
        {
            if (await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is not { } organization)
            {
                return ManagementErrors.NotFound;
            }

            await PutAsync(transaction, CurrentOrganizations, userId, organization.Id, Id, cancellationToken).ConfigureAwait(false);
            await TouchAccountAsync(transaction, userId, cancellationToken).ConfigureAwait(false);
            return organization;
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
            if (access is not OrganizationMember)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    OrganizationMember => throw new UnreachableException(),
                };
            }

            if (await transaction.GetVersionAsync(ProjectOrganizations, id, cancellationToken).ConfigureAwait(false) is not null)
            {
                if (await GetAsync(transaction, ProjectOrganizations, id, Id, cancellationToken).ConfigureAwait(false) == organizationId
                    && await GetAsync(transaction, Projects(organizationId), id, TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false) is { } known
                    && known.Name == name)
                {
                    return known;
                }

                return ManagementErrors.IdTaken;
            }

            if (await GetAsync(transaction, ProjectNames(organizationId), Key(name), Id, cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementErrors.NameTaken;
            }

            await PutAsync(transaction, ProjectOrganizations, project.Id, organizationId, Id, cancellationToken).ConfigureAwait(false);
            await TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, ProjectNames(organizationId), Key(name), project.Id, Id, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Projects(organizationId), project.Id, project, TerraBaseJsonContext.Default.Project, cancellationToken)
                .ConfigureAwait(false);
            return project;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ListProjectsResult> ListProjectsAsync(
        string userId, string organizationId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ListProjectsResult>(async transaction =>
            await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is null
                ? ManagementErrors.NotFound
                : await ListAsync(transaction, Projects(organizationId), after, limit, TerraBaseJsonContext.Default.Project, project => project.Id, cancellationToken)
                    .ConfigureAwait(false),
            static error => error, cancellationToken);

    public ValueTask<GetProjectResult> GetProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        ReadAsync<GetProjectResult>(async transaction =>
            await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is { } project
                ? project
                : ManagementErrors.NotFound,
            static error => error, cancellationToken);

    public async ValueTask<RenameProjectResult> RenameProjectAsync(
        string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken) =>
        await WriteAsync<RenameProjectResult>(async transaction =>
        {
            var access = await WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (access is not OrganizationMember)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    OrganizationMember => throw new UnreachableException(),
                };
            }

            if (await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is not { } project)
            {
                return ManagementErrors.NotFound;
            }

            if (!await RenameAsync(transaction, ProjectNames(organizationId), project.Id, project.Name, name, cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.NameTaken;
            }

            await TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            project = project with { Name = name };
            await PutAsync(transaction, Projects(organizationId), project.Id, project, TerraBaseJsonContext.Default.Project, cancellationToken)
                .ConfigureAwait(false);
            return project;
        }, static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<DeleteProjectResult> DeleteProjectAsync(
        string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await WriteAsync<DeleteProjectResult>(async transaction =>
        {
            var access = await WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (access is not OrganizationMember)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    OrganizationMember => throw new UnreachableException(),
                };
            }

            if (await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is not { } project)
            {
                return ManagementErrors.NotFound;
            }

            if (await AnyAsync(transaction, Databases(project.Id), cancellationToken).ConfigureAwait(false)
                || await AnyAsync(transaction, EngineApiKeyStore.ProjectKeys(project.Id), cancellationToken).ConfigureAwait(false)
                || await AnyAsync(transaction, ProjectBackupDestinations.Collection(project.Id), cancellationToken).ConfigureAwait(false)
                || await AnyAsync(transaction, Features.FileStorage.FileStorageUsage.Buckets(project.Id), cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.NotEmpty;
            }

            await TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(ProjectOrganizations, project.Id, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(ProjectNames(organizationId), Key(project.Name), cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Projects(organizationId), project.Id, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteCollectionsAsync(SystemCatalog.ProjectState(project.Id), cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<CreateDatabaseResult> CreateDatabaseAsync(
        string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken, IReadOnlyList<string>? replicaRegions = null,
        bool allRegions = false) =>
        allRegions || replicaRegions is { Count: > 0 }
            ? new Conflict("databases serve only in their home region until replicas are available", "replicas_unavailable")
            : await AddDatabaseAsync(
            transaction => MemberProjectAsync(transaction, userId, organizationId, projectId, cancellationToken), name, sourceId: null, branchedFrom: null, cancellationToken,
            placement: new DatabasePlacement(options.Region, [], 1))
            .ConfigureAwait(false) switch
            {
                AddedDatabase added => added.Database,
                NotFound error => error,
                Forbidden error => error,
                Conflict error => error,
                QuotaExceeded => throw new UnreachableException(),
                Unavailable error => error,
            };

    public ValueTask<SetDatabaseRegionsResult> SetDatabaseRegionsAsync(
        string userId, string organizationId, string projectId, string databaseId, IReadOnlyList<string> replicaRegions, CancellationToken cancellationToken,
        bool allRegions = false) =>
        WriteAsync(
            transaction => SetDatabaseRegionsAsync(transaction, userId, organizationId, projectId, databaseId, replicaRegions, cancellationToken, allRegions), static error => error,
            cancellationToken);

    private async ValueTask<SetDatabaseRegionsResult> SetDatabaseRegionsAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string databaseId, IReadOnlyList<string> replicaRegions,
        CancellationToken cancellationToken, bool allRegions = false)
    {
        var project = await MemberProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
        if (project is not TenantContext tenant)
        {
            return project switch
            {
                NotFound error => error,
                Forbidden error => error,
                TenantContext => throw new UnreachableException(),
            };
        }

        if (await GetAsync(transaction, Databases(projectId), databaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not { } database)
        {
            return ManagementErrors.NotFound;
        }

        if (allRegions || replicaRegions.Count > 0)
        {
            return new Conflict("databases serve only in their home region until replicas are available", "replicas_unavailable");
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
        await PutAsync(transaction, Databases(projectId), databaseId, placed, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false);
        await TouchOrganizationAsync(transaction, tenant.OrganizationId, cancellationToken).ConfigureAwait(false);
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
                    : await GetAsync(transaction, Databases(projectId), access.DatabaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false);
                return new ManagementPage<ManagedDatabase>(restricted is not null && (after is null || string.CompareOrdinal(restricted.Id, after) > 0)
                    ? [restricted] : [], null);
            }

            return await ListAsync(transaction, Databases(projectId), after, limit, TerraBaseJsonContext.Default.ManagedDatabase, database => database.Id, cancellationToken)
                .ConfigureAwait(false);
        }, static error => error, cancellationToken);

    public ValueTask<GetDatabaseResult> GetDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        ReadAsync<GetDatabaseResult>(async transaction =>
            await GetDatabaseAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is { } database
                ? database
                : ManagementErrors.NotFound,
            static error => error, cancellationToken);

    public ValueTask<DatabaseTenantResult> GetDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        ReadAsync<DatabaseTenantResult>(async transaction =>
            await MemberDatabaseTenantAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is { } tenant
                ? tenant
                : ManagementErrors.NotFound,
            static error => error, cancellationToken);

    public ValueTask<WritableDatabaseTenantResult> GetWritableDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        ReadAsync<WritableDatabaseTenantResult>(async transaction =>
        {
            var access = await WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (access is not OrganizationMember)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    OrganizationMember => throw new UnreachableException(),
                };
            }

            return await MemberDatabaseTenantAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is { } tenant
                ? tenant
                : ManagementErrors.NotFound;
        }, static error => error, cancellationToken);

    public ValueTask<WritableProjectResult> GetWritableProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        ReadAsync<WritableProjectResult>(async transaction =>
        {
            var access = await WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (access is not OrganizationMember)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    OrganizationMember => throw new UnreachableException(),
                };
            }

            return await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is { } project
                ? project
                : ManagementErrors.NotFound;
        }, static error => error, cancellationToken);

    internal static ValueTask<AuthorizedOrganization> WritableAsync(
        IStorageTransaction transaction, string userId, string organizationId, CancellationToken cancellationToken) =>
        AuthorizeAsync(transaction, userId, organizationId, OrganizationRole.Member, cancellationToken);

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

        return await DeleteRecordedAsync(
            deletable.Storage,
            async (transaction, token) =>
                await UnregisterDatabaseAsync(transaction, userId, organizationId, projectId, databaseId, options.Region, token).ConfigureAwait(false) is TenantContext,
            cancellationToken).ConfigureAwait(false) switch
        {
            bool deleted => deleted ? new Done() : ManagementErrors.NotFound,
            Unavailable refused => refused,
        };
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
        var project = await MemberProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
        if (project is not TenantContext tenant)
        {
            return project switch
            {
                NotFound error => error,
                Forbidden error => error,
                TenantContext => throw new UnreachableException(),
            };
        }

        if (await GetAsync(transaction, Databases(projectId), databaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not { } database)
        {
            return ManagementErrors.NotFound;
        }

        return CheckDeletionPlacement(database, region) is Conflict refused ? refused : new DeletableDatabase(tenant with { DatabaseId = database.Id }, database);
    }

    private async ValueTask<UnregisterResult> UnregisterDatabaseAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string databaseId, string region, CancellationToken cancellationToken) =>
        await DeletableAsync(transaction, userId, organizationId, projectId, databaseId, region, cancellationToken).ConfigureAwait(false) switch
        {
            DeletableDatabase deletable => await UnregisterRecordAsync(transaction, deletable.Storage, deletable.Record, cancellationToken, customerDeletion: true).ConfigureAwait(false),
            NotFound error => error,
            Forbidden error => error,
            Conflict error => error,
        };

    private async ValueTask<TenantContext> UnregisterRecordAsync(
        IStorageTransaction transaction, TenantContext storage, ManagedDatabase database, CancellationToken cancellationToken, bool customerDeletion = false)
    {
        if (customerDeletion)
        {
            await RecordDatabasePurgeAsync(transaction, storage, database, cancellationToken).ConfigureAwait(false);
            await BackupDestinationHistory.CaptureAsync(transaction, storage, cancellationToken).ConfigureAwait(false);
        }
        await PutAsync(transaction, PendingDatabaseCleanup, database.Id, storage, TerraBaseJsonContext.Default.TenantContext, cancellationToken).ConfigureAwait(false);
        if (await GetAsync(transaction, DatabaseNames(database.ProjectId), database.Name, Id, cancellationToken).ConfigureAwait(false) == database.Id)
        {
            await transaction.DeleteAsync(DatabaseNames(database.ProjectId), database.Name, cancellationToken).ConfigureAwait(false);
        }
        await transaction.DeleteAsync(Databases(database.ProjectId), database.Id, cancellationToken).ConfigureAwait(false);
        await StorageSweep.AddAsync(transaction, new SweepEntry(StorageSweep.DatabaseBackups, storage.OrganizationId, database.Id), cancellationToken).ConfigureAwait(false);
        if (await transaction.TombstoneAsync(EngineManagedBackupService.BackedUpDatabases, database.Id, cancellationToken).ConfigureAwait(false) is null)
        {
            await PutAsync(transaction, EngineManagedBackupService.BackedUpDatabases, database.Id, DatabaseOwner.Of(storage), TerraBaseJsonContext.Default.DatabaseOwner, cancellationToken)
                .ConfigureAwait(false);
            _ = await transaction.TombstoneAsync(EngineManagedBackupService.BackedUpDatabases, database.Id, cancellationToken).ConfigureAwait(false);
        }

        await transaction.DeleteCollectionsAsync(SystemCatalog.DatabaseState(database.Id), cancellationToken).ConfigureAwait(false);
        await TouchOrganizationAsync(transaction, storage.OrganizationId, cancellationToken).ConfigureAwait(false);
        return storage;
    }

    public async ValueTask<BranchDatabaseResult> BranchDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, string name, CancellationToken cancellationToken)
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
            transaction => MemberProjectAsync(transaction, userId, organizationId, projectId, cancellationToken), name, source.Id, source.Id, cancellationToken,
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
        string userId, string organizationId, string projectId, string sourceId, string name, string? branchedFrom, CancellationToken cancellationToken)
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
            transaction => MemberProjectAsync(transaction, userId, organizationId, projectId, cancellationToken), name, sourceId, branchedFrom, cancellationToken, placement: placement)
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

    private async ValueTask<AddDatabaseResult> AddDatabaseAsync(
        Func<IStorageTransaction, ValueTask<ProjectTenant>> project, string name, string? sourceId, string? branchedFrom, CancellationToken cancellationToken,
        string? id = null, DatabasePlacement? placement = null, DateTimeOffset? createdAt = null)
    {
        var allowance = sourceId is not null || (placement?.Home ?? options.Region) == options.Region
            ? new AllowanceCheck(databaseAllowance.Limit, options.Region, await ServingAsync(cancellationToken).ConfigureAwait(false))
            : null;

        var precheck = await WithUnitAsync(() => PrecheckAsync(project, name, allowance, cancellationToken), project, allowance, cancellationToken).ConfigureAwait(false);

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
            placement.AllRegions);
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

            registered = await WithUnitAsync(() => RegisterAsync(project, database, tenant, allowance, cancellationToken), project, allowance, cancellationToken).ConfigureAwait(false);

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
        Func<IStorageTransaction, ValueTask<ProjectTenant>> project, string name, AllowanceCheck? allowance, CancellationToken cancellationToken) =>
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
        Func<IStorageTransaction, ValueTask<ProjectTenant>> project, ManagedDatabase database, TenantContext tenant, AllowanceCheck? allowance, CancellationToken cancellationToken) =>
        WriteAsync<AddDatabasePrecheck>(async transaction =>
        {
            var registration = await RegisterDatabaseAsync(transaction, project, database, allowance is null ? null : allowance with { Registering = time.GetUtcNow() }, cancellationToken)
                .ConfigureAwait(false);
            if (registration is TenantContext && ProjectAccess.Current is { } access)
            {
                await RecordAsync(transaction, tenant.OrganizationId, access.Actor,
                    database.BranchedFrom is null ? "database.created" : "database.branched", database.Id, database.Name, cancellationToken).ConfigureAwait(false);
            }

            return registration switch
            {
                TenantContext free => free,
                NotFound error => error,
                Forbidden error => error,
                Conflict error => error,
            };
        }, static error => error, cancellationToken);

    private async ValueTask<AddDatabasePrecheck> WithUnitAsync(
        Func<ValueTask<AddDatabasePrecheck>> check, Func<IStorageTransaction, ValueTask<ProjectTenant>> project, AllowanceCheck? allowance, CancellationToken cancellationToken)
    {
        var result = await check().ConfigureAwait(false);
        for (var attempt = 0; attempt < MaxUnitAcquisitions && allowance is not null && result is Conflict conflict && conflict == ManagementErrors.DatabaseAllowanceElsewhere; attempt++)
        {
            if (await AcquireUnitAsync(project, allowance, cancellationToken).ConfigureAwait(false) is { } refusal)
            {
                return refusal;
            }

            result = await check().ConfigureAwait(false);
        }

        return result;
    }

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

                    await PutAsync(transaction, PendingDatabaseCleanup, storage.DatabaseId!, storage, TerraBaseJsonContext.Default.TenantContext, token).ConfigureAwait(false);
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
            await transaction.PutBatchAsync(
            [
                (DatabaseNames(database.ProjectId), database.Name, JsonSerializer.SerializeToUtf8Bytes(database.Id, Id)),
                (Databases(database.ProjectId), database.Id,
                    EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(database, TerraBaseJsonContext.Default.ManagedDatabase), database.Id, nested: true)),
                (EngineManagedBackupService.BackedUpDatabases, database.Id,
                    JsonSerializer.SerializeToUtf8Bytes(DatabaseOwner.Of(tenant with { DatabaseId = database.Id }), TerraBaseJsonContext.Default.DatabaseOwner)),
            ], new (long Version, DocumentVersion? Previous)[3], cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(PendingDatabaseCleanup, database.Id, cancellationToken).ConfigureAwait(false);
            await TouchOrganizationAsync(transaction, tenant.OrganizationId, cancellationToken).ConfigureAwait(false);
            if (allowance is not null)
            {
                await PutAsync(transaction, DatabaseUnits(tenant.OrganizationId), UnitId(database.ProjectId, database.Id), new DatabaseUnit(allowance.Region),
                    TerraBaseJsonContext.Default.DatabaseUnit, cancellationToken).ConfigureAwait(false);
            }
        }

        return check;
    }

    private async ValueTask AbandonDatabaseAsync(TenantContext storage)
    {
        var registered = await ReadAsync<RecordedDeletion>(async transaction =>
            await transaction.GetAsync(Databases(storage.ProjectId!), storage.DatabaseId!, CancellationToken.None).ConfigureAwait(false) is not null
            && await transaction.GetAsync(PendingDatabaseCleanup, storage.DatabaseId!, CancellationToken.None).ConfigureAwait(false) is null,
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
        IStorageTransaction transaction, Func<IStorageTransaction, ValueTask<ProjectTenant>> project, string name, CancellationToken cancellationToken,
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

        if (await NameHolderAsync(transaction, tenant.ProjectId!, name, cancellationToken).ConfigureAwait(false) is not null)
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

        var escrow = await EscrowAsync(transaction, tenant.OrganizationId, allowed, allowance, cancellationToken).ConfigureAwait(false);
        return escrow.Live >= allowed ? ManagementErrors.DatabaseLimit(PlanName(tenant.Plan), allowed)
            : escrow.Rights < 1 ? ManagementErrors.DatabaseAllowanceElsewhere
            : null;
    }

    private static async ValueTask<TenantContext?> ProjectTenantAsync(
        IStorageTransaction transaction, Organization? organization, string projectId, CancellationToken cancellationToken) =>
        organization is not null
        && (ProjectAccess.Current is not { } access || access.ProjectId == projectId && access.OrganizationId == organization.Id)
        && await GetAsync(transaction, Projects(organization.Id), projectId, TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false) is not null
            ? new TenantContext(organization.Id, organization.Plan, projectId)
            : null;

    private static async ValueTask<ProjectTenant> MemberProjectAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, CancellationToken cancellationToken)
    {
        var access = await WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
        if (access is not OrganizationMember)
        {
            return access switch
            {
                NotFound error => error,
                Forbidden error => error,
                OrganizationMember => throw new UnreachableException(),
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
        && await GetAsync(transaction, Databases(project.ProjectId!), databaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
            .ConfigureAwait(false) is not null
            ? project with { DatabaseId = databaseId }
            : null;

    internal static async ValueTask<TenantContext?> MemberDatabaseTenantAsync(
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
        GetAsync(transaction, Databases(projectId), databaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken);

    internal static async ValueTask<PlanTier> PlanUncheckedAsync(IStorageTransaction transaction, string organizationId, CancellationToken cancellationToken) =>
        (await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false))?.Plan
        ?? PlanTier.Hobby;

    private static async ValueTask<Organization?> GetOrganizationAsync(
        IStorageTransaction transaction, string userId, string organizationId, CancellationToken cancellationToken) =>
        !ProjectActor(userId, organizationId) && await GetMemberAsync(transaction, organizationId, userId, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false);

    internal static async ValueTask<Project?> GetProjectAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        ProjectAccess.Current is { } access && access.ProjectId != projectId
            || await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await GetAsync(transaction, Projects(organizationId), projectId, TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false);

    internal static async ValueTask<ManagedDatabase?> GetDatabaseAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        ProjectAccess.Current is { DatabaseName: not null } access && access.DatabaseId != databaseId
            || await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await GetAsync(transaction, Databases(projectId), databaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                .ConfigureAwait(false);

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
        var items = new List<T>(page.Documents.Count);
        foreach (var document in page.Documents)
        {
            if (typeof(T) != typeof(ManagedDatabase) || !await DatabasePurge.IsDeletedAsync(transaction, document.Id, cancellationToken).ConfigureAwait(false))
            {
                items.Add(EntityPayload.Read(document, type)!);
            }
        }
        return new ManagementPage<T>(items, page.HasMore ? id(EntityPayload.Read(page.Documents[^1], type)!) : null);
    }

    private static async ValueTask<T?> GetAsync<T>(
        IStorageTransaction transaction, string collection, string id, JsonTypeInfo<T> type, CancellationToken cancellationToken)
        where T : class
    {
        return await SystemCatalog.GetAsync(transaction, collection, id, type, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask PutAsync<T>(
        IStorageTransaction transaction, string collection, string id, T value, JsonTypeInfo<T> type, CancellationToken cancellationToken) =>
        await transaction.PutAsync(collection, id, EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(value, type), id, nested: true), cancellationToken).ConfigureAwait(false);

    private async ValueTask<T> ReadAsync<T>(Func<IStorageTransaction, ValueTask<T>> read, Func<Unavailable, T> unavailable, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (CommitTransaction.Value is { } commit)
        {
            return await CheckReadAsync(commit, cancellationToken).ConfigureAwait(false) switch
            {
                Done => await read(commit).ConfigureAwait(false),
                Unavailable refused => unavailable(refused),
            };
        }

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
        RequireNoCommit();
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
        if (CommitTransaction.Value is { } commit)
        {
            return await CheckReadAsync(commit, cancellationToken).ConfigureAwait(false) switch
            {
                Done => await write(commit).ConfigureAwait(false),
                Unavailable refused => unavailable(refused),
            };
        }

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
        RequireNoCommit();
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

            if (version is not null || await HasCatalogStateAsync(transaction, cancellationToken).ConfigureAwait(false))
            {
                logger.ZLogError($"The management catalog has a format this build does not support; discard the affected data");
                return ManagementErrors.CatalogUnsupported;
            }

            await PutAsync(transaction, Catalog, CatalogVersionId, CatalogVersion, Id, cancellationToken).ConfigureAwait(false);
            return new Done();
        }, cancellationToken, keep: static result => result is Done).ConfigureAwait(false);
        if (accepted is Done)
        {
            _catalogCurrent = true;
        }

        return accepted;
    }

    private static async ValueTask<bool> HasCatalogStateAsync(IStorageTransaction transaction, CancellationToken cancellationToken)
    {
        foreach (var collection in new[] { Catalog, Organizations, CurrentOrganizations, ProjectOrganizations, PendingDatabaseCleanup,
            InvitationTokens, OrganizationVersions, AccountVersions, CommitExpiries, OwnershipTransfers, AuditRetentions,
            AuditSequences, InvitationSequences, "organization_names", "backed_up_databases", "backup_sequence", "storage_sweep",
            "dashboard_query_shapes", "dashboard_query_shapes_flush" })
        {
            if (await transaction.HasCollectionStateAsync(collection, prefix: false, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        foreach (var prefix in new[] { "organizations/", "users/", "projects/", "databases/", "dashboard/" })
        {
            if (await transaction.HasCollectionStateAsync(prefix, prefix: true, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
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
            items.AddRange(page.Documents.Select(document => EntityPayload.Read(document, type)!));
            after = page.Documents.Count > 0 ? page.Documents[^1].Id : after;
        }
        while (page.HasMore);

        return items;
    }

    private string NewId() => EntityIds.New(time).ToString();

    private static string Key(string name) => name.ToLowerInvariant();

    private static string OrganizationNames(string userId) => $"users/{userId}/organization_names";

    private static string UserOrganizations(string userId) => $"users/{userId}/organizations";

    private static string Members(string organizationId) => $"organizations/{organizationId}/members";

    private static string Projects(string organizationId) => $"organizations/{organizationId}/projects";

    private static string ProjectNames(string organizationId) => $"organizations/{organizationId}/project_names";

    private static string Databases(string projectId) => $"projects/{projectId}/databases";

    private static string DatabaseNames(string projectId) => $"projects/{projectId}/database_names";

    private sealed record AddedDatabase(ManagedDatabase Database, TenantContext Tenant, bool Existing = false);

    private sealed record DeletableDatabase(TenantContext Storage, ManagedDatabase Record);

    private readonly union ProjectTenant(TenantContext, NotFound, Forbidden);

    private readonly union AddDatabaseCheck(TenantContext, NotFound, Forbidden, Conflict);

    private readonly union AddDatabasePrecheck(TenantContext, NotFound, Forbidden, Conflict, Unavailable);

    private readonly union AddDatabaseResult(AddedDatabase, NotFound, Forbidden, Conflict, QuotaExceeded, Unavailable);

    private readonly union DeletableDatabaseCheck(DeletableDatabase, NotFound, Forbidden, Conflict);

    private readonly union DeletableDatabaseResult(DeletableDatabase, NotFound, Forbidden, Conflict, Unavailable);

    private readonly union UnregisterResult(TenantContext, NotFound, Forbidden, Conflict);
}

internal sealed record DatabaseOwner(string OrganizationId, string ProjectId, string DatabaseId)
{
    public static DatabaseOwner Of(TenantContext database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new(database.OrganizationId, database.ProjectId!, database.DatabaseId!);
    }
}
