using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Billing.Internal;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class DeletionRelatedRecords(IDashboardState state, IDbContextFactory<DashboardDbContext> storage)
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

            await CleanNotificationsAsync(organizationId, null, cancellationToken);
        }

        public async Task CleanProjectAsync(string organizationId, string projectId, CancellationToken cancellationToken)
        {
            await CleanNotificationsAsync(organizationId, projectId, cancellationToken);
        }

        private async Task CleanNotificationsAsync(string organizationId, string? projectId, CancellationToken cancellationToken)
        {
            foreach (var listed in await state.ListAsync(AccountService.AccountsTable, cancellationToken))
            {
                if (AccountService.IsDeletionFence(listed))
                {
                    continue;
                }

                var known = AccountService.Read<AccountRecord>(listed);
                if (string.IsNullOrWhiteSpace(known.UserId))
                {
                    throw new IOException("Notification owner is invalid.");
                }

                var key = AccountService.AccountStorageKey(known.UserId);
                for (var attempt = 0; ; attempt++)
                {
                    var row = await state.GetAsync(AccountService.AccountsTable, key, cancellationToken);
                    if (row is null || AccountService.IsDeletionFence(row))
                    {
                        break;
                    }

                    var account = AccountService.Read<AccountRecord>(row);
                    if (row.Id != key || account.UserId != known.UserId)
                    {
                        throw new IOException("Notification owner changed.");
                    }

                    if (account.Notifications.RemoveAll(note => note.OrganizationId == organizationId && (projectId is null || note.ProjectId == projectId)) == 0)
                    {
                        break;
                    }

                    try
                    {
                        await state.PutAsync(AccountService.AccountsTable, key, JsonSerializer.SerializeToElement(account), StatePrecondition.Matches(row.Version), cancellationToken);
                        break;
                    }
                    catch (StateConflictException) when (attempt < 3) { }
                }
            }
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
