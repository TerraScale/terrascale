using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class DeletionRelatedRecords(IDbContextFactory<DashboardDbContext> storage, AccountStore accounts)
    {
        public async Task CleanAccountAsync(string subject, CancellationToken cancellationToken)
        {
            await Features.Data.SqlWorkspace.EraseAsync(storage, row => row.UserId == subject, cancellationToken);
        }

        public async Task CleanOrganizationAsync(string organizationId, CancellationToken cancellationToken)
        {
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                await db.InternalBilling.Where(row => row.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
                await db.BillingChanges.Where(row => row.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
                await db.AllowanceAlerts.Where(row => row.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
            }

            await accounts.RemoveNotificationsAsync(organizationId, null, cancellationToken);
        }

        public Task CleanProjectAsync(string organizationId, string projectId, CancellationToken cancellationToken)
        {
            return accounts.RemoveNotificationsAsync(organizationId, projectId, cancellationToken);
        }
    }
}
