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
                var allowances = await db.AllowanceAlerts.AsNoTracking().Where(row => row.OrganizationId == organizationId)
                    .Select(row => "allowance/" + row.Id).ToListAsync(cancellationToken);
                var plan = "plan/" + organizationId;
                await db.BackgroundJobs.Where(row => row.Id == plan || allowances.Contains(row.Id)).ExecuteDeleteAsync(cancellationToken);
                await db.AllowanceAlerts.Where(row => row.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
            }

            await accounts.RemoveNotificationsAsync(organizationId, null, cancellationToken);
        }

        public async Task CleanProjectAsync(string organizationId, string projectId, CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var prefix = "log-drain/" + projectId + "/";
            var alerts = "alerts/" + projectId;
            await db.BackgroundJobs.Where(row => row.Id.StartsWith(prefix) || row.Id == alerts).ExecuteDeleteAsync(cancellationToken);
            await accounts.RemoveNotificationsAsync(organizationId, projectId, cancellationToken);
        }
    }
}
