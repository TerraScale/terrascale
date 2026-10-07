using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>
/// The catalog as a <see cref="ITenantDirectory"/> for API keys, which act for an organization rather than a user.
/// </summary>
internal sealed partial class EngineManagementService
{
    public async ValueTask<TenantContext?> FindDatabaseAsync(string organizationId, string projectId, string database, CancellationToken cancellationToken)
    {
        var tenant = await ReadAsync(transaction => FindDatabaseAsync(transaction, organizationId, projectId, database, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        // A follower serves a database the replicated catalog registers before any of its documents arrived: an empty
        // local file, which replication then fills like any other (docs/decisions/0009).
        // A deletion it applies meanwhile could miss the new file: one this call created is checked again, and deleted with it
        // once the catalog, at a version this build reads, no longer registers the database.
        if (tenant is not null && options.CatalogPrimary is not null && await engine.OpenDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false) is null
            && await engine.CreateDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false)
            && await RegisteredAsync(tenant, cancellationToken).ConfigureAwait(false) is false)
        {
            _ = await engine.DeleteDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return tenant;
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

    public ValueTask<bool?> RegisteredAsync(TenantContext database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.ProjectId is null || database.DatabaseId is null
            ? ValueTask.FromResult<bool?>(false)
            : ReadCatalogAsync<bool?>(async transaction =>
                !Interprets(await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false))
                    ? null
                    : await ScopeExistsUncheckedAsync(transaction, database.OrganizationId, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false),
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
