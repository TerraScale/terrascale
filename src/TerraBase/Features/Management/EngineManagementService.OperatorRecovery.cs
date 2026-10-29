using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.UserAuth;
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
            if (authorityToken is not null)
            {
                return await RecoveryEnrolledTenantAsync(transaction, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is { } enrolled
                    ? enrolled : ManagementErrors.NotFound;
            }

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

            return await AuthorizedDatabaseTenantAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is { } tenant
                ? tenant
                : ManagementErrors.NotFound;
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (local is not TenantContext before || authorityToken is null)
        {
            return local;
        }

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

        return await ReadAsync<WritableDatabaseTenantResult>(async transaction =>
            await RecoveryEnrolledTenantAsync(transaction, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is { } after && before == after
                ? after : ManagementErrors.NotFound, static error => error, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TenantContext?> RecoveryEnrolledTenantAsync(
        IStorageTransaction transaction, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        options.AuthIssuer is not null
        && await IsProjectRegisteredAsync(transaction, projectId, organizationId, cancellationToken).ConfigureAwait(false)
        && await GetAsync(transaction, ProjectOrganizations, projectId, Id, cancellationToken).ConfigureAwait(false) == organizationId
        && await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false)
            is { CreatedBy: OperatorHandler.KeyId }

            ? await DatabaseTenantUncheckedAsync(transaction, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) : null;
}
