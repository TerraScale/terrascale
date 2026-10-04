using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>
/// Keeps the management catalog as documents in the <see cref="DatabaseName.System"/> database. Every
/// change runs in one write transaction, which the engine serializes, so membership checks, name checks
/// and writes are atomic. Names are unique per parent through <c>*_names</c> collections keyed by the
/// lowercased name; organization names are unique per creator, so they reveal nothing across tenants.
/// </summary>
/// <remarks>
/// Membership is stored twice: <c>organizations/{id}/members</c> (who belongs) and
/// <c>users/{id}/organizations</c> (what a user lists). Every lookup goes through
/// <see cref="GetOrganizationAsync(IStorageTransaction, string, string, CancellationToken)"/>, which
/// answers <see langword="null"/> to non-members.
/// <para>
/// The catalog is versioned by the <c>catalog/version</c> document. Version 1 (no document) predates
/// membership: its organizations have no members and globally unique names. It is migrated to version 2
/// in one transaction, before the first operation of the process, by assigning every organization to
/// <see cref="TerraBaseOptions.ManagementLegacyOwner"/>; without that setting the API answers 503. Any other
/// version is unsupported: the API answers 503 and the catalog is left unchanged.
/// </para>
/// </remarks>
internal sealed partial class EngineManagementService(
    IStorageEngine engine, TimeProvider time, TerraBaseOptions options, ILogger<EngineManagementService> logger) : IManagementService
{
    private const string Organizations = "organizations";
    private const string CurrentOrganizations = "current_organizations";
    private const string Catalog = "catalog";
    private const string CatalogVersionId = "version";
    private const string CatalogVersion = "2";

    // Version 1 kept one global name index.
    private const string LegacyOrganizationNames = "organization_names";

    // Catalog documents are small; this only bounds a page if one ever is not.
    private const long MaxPageBytes = 1024 * 1024;

    // Documents per page when deleting members or migrating the catalog.
    private const int MemberPage = 100;

    private static readonly JsonTypeInfo<string> Id = TerraBaseJsonContext.Default.String;

    private volatile bool _catalogCurrent;

    public async ValueTask<ManagementResult<Organization>> CreateOrganizationAsync(string userId, string name, CancellationToken cancellationToken)
    {
        var organization = new Organization(NewId(), name, userId, time.GetUtcNow());
        return await WriteAsync<ManagementResult<Organization>>(async transaction =>
        {
            if (await GetAsync(transaction, OrganizationNames(userId), Key(name), Id, cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementError.NameTaken;
            }

            await PutAsync(transaction, OrganizationNames(userId), Key(name), organization.Id, Id, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Organizations, organization.Id, organization, TerraBaseJsonContext.Default.Organization, cancellationToken)
                .ConfigureAwait(false);
            await PutAsync(transaction, Members(organization.Id), userId, userId, Id, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, UserOrganizations(userId), organization.Id, organization.Id, Id, cancellationToken).ConfigureAwait(false);
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
        }, new ManagementPage<Organization>([], null), cancellationToken);

    public ValueTask<Organization?> GetOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        ReadAsync(transaction => GetOrganizationAsync(transaction, userId, organizationId, cancellationToken), null, cancellationToken);

    public async ValueTask<ManagementResult<Organization>> RenameOrganizationAsync(
        string userId, string organizationId, string name, CancellationToken cancellationToken) =>
        await WriteAsync<ManagementResult<Organization>>(async transaction =>
        {
            if (await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is not { } organization)
            {
                return ManagementError.NotFound;
            }

            if (!await RenameAsync(transaction, OrganizationNames(organization.CreatedBy), organization.Id, organization.Name, name, cancellationToken)
                .ConfigureAwait(false))
            {
                return ManagementError.NameTaken;
            }

            organization = organization with { Name = name };
            await PutAsync(transaction, Organizations, organization.Id, organization, TerraBaseJsonContext.Default.Organization, cancellationToken)
                .ConfigureAwait(false);
            return organization;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ManagementError> DeleteOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        await WriteAsync(async transaction =>
        {
            if (await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is not { } organization)
            {
                return ManagementError.NotFound;
            }

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
                    await transaction.DeleteAsync(UserOrganizations(member.Id), organization.Id, cancellationToken).ConfigureAwait(false);
                    await transaction.DeleteAsync(Members(organization.Id), member.Id, cancellationToken).ConfigureAwait(false);
                }
            }
            while (members.HasMore);

            await transaction.DeleteAsync(OrganizationNames(organization.CreatedBy), Key(organization.Name), cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Organizations, organization.Id, cancellationToken).ConfigureAwait(false);
            return ManagementError.None;
        }, cancellationToken).ConfigureAwait(false);

    public ValueTask<Organization?> GetCurrentOrganizationAsync(string userId, CancellationToken cancellationToken) =>
        ReadAsync(async transaction =>
            await GetAsync(transaction, CurrentOrganizations, userId, Id, cancellationToken).ConfigureAwait(false) is { } organizationId
                ? await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false)
                : null,
            null,
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
            return organization;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ManagementResult<Project>> CreateProjectAsync(
        string userId, string organizationId, string name, CancellationToken cancellationToken)
    {
        var project = new Project(NewId(), organizationId, name, time.GetUtcNow());
        return await WriteAsync<ManagementResult<Project>>(async transaction =>
        {
            if (await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is null)
            {
                return ManagementError.NotFound;
            }

            if (await GetAsync(transaction, ProjectNames(organizationId), Key(name), Id, cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementError.NameTaken;
            }

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
            null,
            cancellationToken);

    public ValueTask<Project?> GetProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        ReadAsync(transaction => GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken), null, cancellationToken);

    public async ValueTask<ManagementResult<Project>> RenameProjectAsync(
        string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken) =>
        await WriteAsync<ManagementResult<Project>>(async transaction =>
        {
            if (await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is not { } project)
            {
                return ManagementError.NotFound;
            }

            if (!await RenameAsync(transaction, ProjectNames(organizationId), project.Id, project.Name, name, cancellationToken).ConfigureAwait(false))
            {
                return ManagementError.NameTaken;
            }

            project = project with { Name = name };
            await PutAsync(transaction, Projects(organizationId), project.Id, project, TerraBaseJsonContext.Default.Project, cancellationToken)
                .ConfigureAwait(false);
            return project;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ManagementError> DeleteProjectAsync(
        string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await WriteAsync(async transaction =>
        {
            if (await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is not { } project)
            {
                return ManagementError.NotFound;
            }

            if (await AnyAsync(transaction, Databases(project.Id), cancellationToken).ConfigureAwait(false))
            {
                return ManagementError.NotEmpty;
            }

            await transaction.DeleteAsync(ProjectNames(organizationId), Key(project.Name), cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Projects(organizationId), project.Id, cancellationToken).ConfigureAwait(false);
            return ManagementError.None;
        }, cancellationToken).ConfigureAwait(false);

    public ValueTask<ManagementResult<ManagedDatabase>> CreateDatabaseAsync(
        string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken) =>
        AddDatabaseAsync(userId, organizationId, projectId, name, source: null, cancellationToken);

    public ValueTask<ManagementPage<ManagedDatabase>?> ListDatabasesAsync(
        string userId, string organizationId, string projectId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ManagementPage<ManagedDatabase>?>(async transaction =>
            await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null
                ? null
                : await ListAsync(
                    transaction, Databases(projectId), after, limit, TerraBaseJsonContext.Default.ManagedDatabase, database => database.Id, cancellationToken)
                    .ConfigureAwait(false),
            null,
            cancellationToken);

    public ValueTask<ManagedDatabase?> GetDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        ReadAsync(transaction => GetDatabaseAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken), null, cancellationToken);

    public async ValueTask<ManagementError> DeleteDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken)
    {
        var database = await WriteAsync(async transaction =>
        {
            if (await GetDatabaseAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is not { } database)
            {
                return null;
            }

            await transaction.DeleteAsync(DatabaseNames(projectId), database.Name, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Databases(projectId), database.Id, cancellationToken).ConfigureAwait(false);
            return database;
        }, cancellationToken).ConfigureAwait(false);
        if (database is null)
        {
            return ManagementError.NotFound;
        }

        // The catalog no longer names it: finish even if the caller goes away, or the file leaks.
        await engine.DeleteDatabaseAsync(Storage(database), CancellationToken.None).ConfigureAwait(false);
        return ManagementError.None;
    }

    public async ValueTask<ManagementResult<ManagedDatabase>> BranchDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, string name, CancellationToken cancellationToken) =>
        await GetDatabaseAsync(userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is { } source
            ? await AddDatabaseAsync(userId, organizationId, projectId, name, source, cancellationToken).ConfigureAwait(false)
            : ManagementError.NotFound;

    // Creates (or copies) the storage first and registers it after, deleting it again when registration
    // fails, so the catalog never names a database that does not exist.
    private async ValueTask<ManagementResult<ManagedDatabase>> AddDatabaseAsync(
        string userId, string organizationId, string projectId, string name, ManagedDatabase? source, CancellationToken cancellationToken)
    {
        // Checked again when registering; this avoids creating storage for a request that cannot succeed.
        var precheck = await ReadAsync(
            transaction => CanAddDatabaseAsync(transaction, userId, organizationId, projectId, name, cancellationToken),
            ManagementError.NotFound,
            cancellationToken).ConfigureAwait(false);
        if (precheck != ManagementError.None)
        {
            return precheck;
        }

        var id = NewId();
        var database = new ManagedDatabase(id, projectId, name, "db-" + id.Replace("-", "", StringComparison.Ordinal), source?.Id, time.GetUtcNow());
        var storage = Storage(database);
        var created = source is null
            ? await engine.CreateDatabaseAsync(storage, cancellationToken).ConfigureAwait(false)
            : await engine.CopyDatabaseAsync(Storage(source), storage, cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            // A new random name always creates; only a branch source deleted meanwhile gets here.
            return ManagementError.NotFound;
        }

        ManagementError error;
        try
        {
            error = await WriteAsync(async transaction =>
            {
                var check = await CanAddDatabaseAsync(transaction, userId, organizationId, projectId, name, cancellationToken).ConfigureAwait(false);
                if (check == ManagementError.None)
                {
                    await PutAsync(transaction, DatabaseNames(projectId), name, database.Id, Id, cancellationToken).ConfigureAwait(false);
                    await PutAsync(transaction, Databases(projectId), database.Id, database, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                        .ConfigureAwait(false);
                }

                return check;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await engine.DeleteDatabaseAsync(storage, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (error != ManagementError.None)
        {
            await engine.DeleteDatabaseAsync(storage, CancellationToken.None).ConfigureAwait(false);
            return error;
        }

        return database;
    }

    private static async ValueTask<ManagementError> CanAddDatabaseAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken)
    {
        if (await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null)
        {
            return ManagementError.NotFound;
        }

        return await GetAsync(transaction, DatabaseNames(projectId), name, Id, cancellationToken).ConfigureAwait(false) is null
            ? ManagementError.None
            : ManagementError.NameTaken;
    }

    // The single authorization point: non-members get null, exactly like a missing organization.
    private static async ValueTask<Organization?> GetOrganizationAsync(
        IStorageTransaction transaction, string userId, string organizationId, CancellationToken cancellationToken) =>
        await GetAsync(transaction, Members(organizationId), userId, Id, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false);

    // A project is looked up under its organization, so a project id under another organization is not found.
    private static async ValueTask<Project?> GetProjectAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await GetAsync(transaction, Projects(organizationId), projectId, TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false);

    private static async ValueTask<ManagedDatabase?> GetDatabaseAsync(
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

    // Reads see a snapshot; before the first write the system database may not exist yet.
    private async ValueTask<T> ReadAsync<T>(Func<IStorageTransaction, ValueTask<T>> read, T empty, CancellationToken cancellationToken)
    {
        await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (await engine.OpenDatabaseAsync(DatabaseName.System, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return empty;
        }

        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await read(transaction).ConfigureAwait(false);
        }
    }

    // Commits whatever the callback wrote; a failure result simply wrote nothing.
    private async ValueTask<T> WriteAsync<T>(Func<IStorageTransaction, ValueTask<T>> write, CancellationToken cancellationToken)
    {
        await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
        return await WriteCatalogAsync(write, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> WriteCatalogAsync<T>(Func<IStorageTransaction, ValueTask<T>> write, CancellationToken cancellationToken)
    {
        await engine.CreateDatabaseAsync(DatabaseName.System, cancellationToken).ConfigureAwait(false);
        var store = await engine.OpenDatabaseAsync(DatabaseName.System, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("the system database disappeared after creation");
        var transaction = await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var result = await write(transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    // Brings the catalog to the current version once per process. Concurrent first calls serialize on the
    // write transaction; the later ones find the version document and change nothing.
    private async ValueTask EnsureCatalogAsync(CancellationToken cancellationToken)
    {
        if (_catalogCurrent)
        {
            return;
        }

        var migrated = await WriteCatalogAsync(async transaction =>
        {
            var version = await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false);
            if (version == CatalogVersion)
            {
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
                await PutAsync(transaction, Members(owned.Id), owned.CreatedBy, owned.CreatedBy, Id, cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, UserOrganizations(owned.CreatedBy), owned.Id, owned.Id, Id, cancellationToken).ConfigureAwait(false);
            }

            await PutAsync(transaction, Catalog, CatalogVersionId, CatalogVersion, Id, cancellationToken).ConfigureAwait(false);
            return legacy.Count;
        }, cancellationToken).ConfigureAwait(false);
        if (migrated > 0)
        {
            LogLegacyCatalogMigrated(logger, migrated);
        }

        _catalogCurrent = true;
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

    private static DatabaseName Storage(ManagedDatabase database) =>
        DatabaseName.TryParse(database.StorageName, out var name) ? name : throw new InvalidOperationException("invalid storage name in the catalog");

    private string NewId() => Guid.CreateVersion7(time.GetUtcNow()).ToString();

    private static string Key(string name) => name.ToLowerInvariant();

    private static string OrganizationNames(string userId) => $"users/{userId}/organization_names";

    private static string UserOrganizations(string userId) => $"users/{userId}/organizations";

    private static string Members(string organizationId) => $"organizations/{organizationId}/members";

    private static string Projects(string organizationId) => $"organizations/{organizationId}/projects";

    private static string ProjectNames(string organizationId) => $"organizations/{organizationId}/project_names";

    private static string Databases(string projectId) => $"projects/{projectId}/databases";

    private static string DatabaseNames(string projectId) => $"projects/{projectId}/database_names";
}
