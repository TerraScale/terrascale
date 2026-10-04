using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>
/// The catalog as a <see cref="ITenantDirectory"/> for API keys, which act for an organization rather than a user.
/// </summary>
internal sealed partial class EngineManagementService
{
    public ValueTask<TenantContext?> FindDatabaseAsync(string organizationId, string projectId, string database, CancellationToken cancellationToken) =>
        ReadAsync(transaction => FindDatabaseAsync(transaction, organizationId, projectId, database, cancellationToken), cancellationToken);

    public async ValueTask<(TenantContext Database, bool Created)?> CreateDatabaseAsync(
        string organizationId, string projectId, string database, CancellationToken cancellationToken)
    {
        var added = await AddDatabaseAsync(
            transaction => OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken), database, source: null, cancellationToken)
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

    public ValueTask<bool> ExistsAsync(string organizationId, string? projectId, string? databaseId, CancellationToken cancellationToken) =>
        ReadAsync(async transaction =>
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
        }, cancellationToken);

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
