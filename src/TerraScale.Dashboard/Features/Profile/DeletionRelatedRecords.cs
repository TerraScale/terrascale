using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Billing.Internal;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class DeletionRelatedRecords(IDashboardState state, IDbContextFactory<DashboardDbContext> storage, AccountStore accounts)
    {
        public async Task CleanAccountAsync(string subject, CancellationToken cancellationToken)
        {
            await Features.Data.SqlWorkspace.EraseAsync(storage, row => row.UserId == subject, cancellationToken);
        }

        public async Task CleanOrganizationAsync(string organizationId, CancellationToken cancellationToken)
        {
            if (await state.GetAsync(InternalBillingService.Table, InternalBillingService.SingletonId, cancellationToken) is { } singleton)
            {
                if (singleton.Id != InternalBillingService.SingletonId)
                {
                    throw new IOException("Internal billing singleton identity changed.");
                }

                if (AccountService.Read<InternalBillingSingleton>(singleton).OrganizationId == organizationId)
                {
                    await state.DeleteAsync(InternalBillingService.Table, InternalBillingService.SingletonId, singleton.Version, cancellationToken);
                }
            }
            await DropAsync<BillingChangeReceipt>(StripeBilling.ChangesTable, organizationId, row => row.OrganizationId == organizationId, cancellationToken);
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                await db.AllowanceAlerts.Where(row => row.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
            }

            await accounts.RemoveNotificationsAsync(organizationId, null, cancellationToken);
        }

        public Task CleanProjectAsync(string organizationId, string projectId, CancellationToken cancellationToken)
        {
            return accounts.RemoveNotificationsAsync(organizationId, projectId, cancellationToken);
        }

        private async Task DropAsync<T>(string table, string key, Func<T, bool> owns, CancellationToken cancellationToken)
        {
            if (await state.GetAsync(table, key, cancellationToken) is not { } row)
            {
                return;
            }

            if (row.Id != key || !owns(AccountService.Read<T>(row)))
            {
                throw new IOException("Related deletion record ownership changed.");
            }

            await state.DeleteAsync(table, key, row.Version, cancellationToken);
        }
    }
}
