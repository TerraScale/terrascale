using System.Diagnostics;
using TerraBase.Errors;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    public ValueTask<WritableDatabaseTenantResult> GetRecoveryDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        ReadAsync<WritableDatabaseTenantResult>(async transaction =>
        {
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
        }, static error => error, cancellationToken);
}
