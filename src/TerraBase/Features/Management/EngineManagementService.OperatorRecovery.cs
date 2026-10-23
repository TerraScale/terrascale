using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    public async ValueTask<WritableDatabaseTenantResult> GetRecoveryDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, string? authorityToken, CancellationToken cancellationToken)
    {
        var local = await ReadAsync<WritableDatabaseTenantResult>(async transaction =>
        {
            // Enrolled projects have storage envelopes, not duplicate ts-auth memberships. Without live proof they
            // remain hidden; the legacy branch retains its original local owner/admin authorization.
            if (authorityToken is not null)
            {
                return await RecoveryEnrolledTenantAsync(transaction, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is { } enrolled
                    ? enrolled : ManagementErrors.NotFound;
            }

            var access = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Admin, cancellationToken).ConfigureAwait(false);
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
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (local is not TenantContext before || authorityToken is null)
        {
            return local;
        }

        // No catalog transaction is held across the live authority request. A legacy tenant must use the original
        // operator request; ts-auth authority cannot admit a legacy storage binding.
        var authority = await projectAuthority.IsRecoveryAdministratorAsync(authorityToken, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
        if (authority is not true)
        {
            return authority switch
            {
                false => ManagementErrors.NotFound,
                Unavailable error => error,
                true => throw new UnreachableException(),
            };
        }

        // A concurrent delete or binding change invalidates this authorization. This never enrolls or writes state.
        return await ReadAsync<WritableDatabaseTenantResult>(async transaction =>
            await RecoveryEnrolledTenantAsync(transaction, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is { } after && before == after
                ? after : ManagementErrors.NotFound, static error => error, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TenantContext?> RecoveryEnrolledTenantAsync(
        IStorageTransaction transaction, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        options.AuthIssuer is not null
        && await IsProjectBoundAsync(transaction, projectId, organizationId, cancellationToken).ConfigureAwait(false)
        && await GetAsync(transaction, ProjectOrganizations, projectId, Id, cancellationToken).ConfigureAwait(false) == organizationId
        && await GetAsync(transaction, Bindings(projectId), "ts_auth", TerraBaseJsonContext.Default.ProjectCatalogBinding, cancellationToken)
            .ConfigureAwait(false) is { AuthorityOrganizationId: null }
        && await CountAllAsync(transaction, Members(organizationId), cancellationToken).ConfigureAwait(false) == 0
            ? await DatabaseTenantUncheckedAsync(transaction, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) : null;
}
