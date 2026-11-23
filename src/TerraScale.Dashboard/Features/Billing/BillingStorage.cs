using System.Diagnostics;
using Dapper;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing;

internal static class BillingStorage
{
    private const string BindingOf = DashboardSql.Billing.Select + " WHERE organization_id = @OrganizationId";
    private const string ChangeOf = DashboardSql.BillingChanges.Select + " WHERE organization_id = @OrganizationId";

    extension(DashboardDb storage)
    {
        public async Task<BillingBinding?> BindingRowAsync(string organizationId, CancellationToken cancellationToken)
        {
            var row = await ServerTiming.ReadAsync((storage, nameof(BillingBinding), organizationId), async () =>
            {
                await using var connection = await storage.OpenAsync(cancellationToken);
                return await connection.QuerySingleOrDefaultAsync<BillingBinding>(BindingOf, new { OrganizationId = organizationId });
            });
            return row?.Copy();
        }

        public async Task<BillingChangeReceipt?> ChangeRowAsync(string organizationId, CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            return await connection.QuerySingleOrDefaultAsync<BillingChangeReceipt>(ChangeOf, new { OrganizationId = organizationId });
        }

        public async Task<bool> InsertAsync<TRow>(TRow row, CancellationToken cancellationToken) where TRow : class
        {
            if (row is IBillingRow versioned)
            {
                versioned.Version = AccountService.NewId();
            }

            await using var connection = await storage.OpenAsync(cancellationToken);
            try
            {
                await InsertRowAsync(connection, row);
                return true;
            }
            catch (Exception error) when (DashboardDb.IsDuplicate(error))
            {
                return false;
            }
            catch (TerraBaseException error)
            {
                throw new IOException("Billing state write failed.", error);
            }
        }

        public async Task<bool> ReplaceAsync<TRow>(TRow row, CancellationToken cancellationToken) where TRow : class, IBillingRow
        {
            var expected = row.Version;
            row.OriginalVersion = expected;
            row.Version = AccountService.NewId();
            await using var connection = await storage.OpenAsync(cancellationToken);
            try
            {
                if (await UpdateRowAsync(connection, row) == 1)
                {
                    return true;
                }

                row.Version = expected;
                return false;
            }
            catch (TerraBaseException error)
            {
                row.Version = expected;
                throw new IOException("Billing state write failed.", error);
            }
        }

        public async Task<bool> RemoveAsync<TRow>(TRow row, CancellationToken cancellationToken) where TRow : class
        {
            if (row is IBillingRow versioned)
            {
                versioned.OriginalVersion = versioned.Version;
            }

            await using var connection = await storage.OpenAsync(cancellationToken);
            try
            {
                return await DeleteRowAsync(connection, row) == 1;
            }
            catch (TerraBaseException error)
            {
                throw new IOException("Billing state write failed.", error);
            }
        }
    }

    private static Task<int> InsertRowAsync(TerraBaseDbConnection connection, object row) => row switch
    {
        BillingBinding binding => connection.ExecuteAsync(DashboardSql.Billing.Insert, binding),
        BillingChangeReceipt change => connection.ExecuteAsync(DashboardSql.BillingChanges.Insert, change),
        BillingSubscription subscription => connection.ExecuteAsync(DashboardSql.BillingSubscriptions.Insert, subscription),
        BillingTerms terms => connection.ExecuteAsync(DashboardSql.BillingTerms.Insert, terms),
        OverageCharge charge => connection.ExecuteAsync(DashboardSql.BillingOverageCharges.Insert, charge),
        SkippedBillingMeter skipped => connection.ExecuteAsync(DashboardSql.BillingSkippedMeters.Insert, skipped),
        StripeEventReceipt receipt => connection.ExecuteAsync(DashboardSql.StripeEventReceipts.Insert, receipt),
        StripeCustomerRow customer => connection.ExecuteAsync(DashboardSql.StripeCustomers.Insert, customer),
        StripeCheckoutRow checkout => connection.ExecuteAsync(DashboardSql.StripeCheckouts.Insert, checkout),
        PendingOrganizationCheckout pending => connection.ExecuteAsync(DashboardSql.PendingOrganizationCheckouts.Insert, pending),
        InternalBillingRow internalBilling => connection.ExecuteAsync(DashboardSql.InternalBilling.Insert, internalBilling),
        _ => throw new UnreachableException(string.Concat("Billing storage has no insert for ", row.GetType().Name, ".")),
    };

    private static Task<int> UpdateRowAsync(TerraBaseDbConnection connection, IBillingRow row) => row switch
    {
        BillingBinding binding => connection.ExecuteAsync(DashboardSql.Billing.Update, binding),
        BillingChangeReceipt change => connection.ExecuteAsync(DashboardSql.BillingChanges.Update, change),
        BillingSubscription subscription => connection.ExecuteAsync(DashboardSql.BillingSubscriptions.Update, subscription),
        StripeEventReceipt receipt => connection.ExecuteAsync(DashboardSql.StripeEventReceipts.Update, receipt),
        PendingOrganizationCheckout pending => connection.ExecuteAsync(DashboardSql.PendingOrganizationCheckouts.Update, pending),
        _ => throw new UnreachableException(string.Concat("Billing storage has no update for ", row.GetType().Name, ".")),
    };

    private static Task<int> DeleteRowAsync(TerraBaseDbConnection connection, object row) => row switch
    {
        BillingBinding binding => connection.ExecuteAsync(DashboardSql.Billing.Delete, binding),
        BillingChangeReceipt change => connection.ExecuteAsync(DashboardSql.BillingChanges.Delete, change),
        BillingSubscription subscription => connection.ExecuteAsync(DashboardSql.BillingSubscriptions.Delete, subscription),
        BillingTerms terms => connection.ExecuteAsync(DashboardSql.BillingTerms.Delete, terms),
        OverageCharge charge => connection.ExecuteAsync(DashboardSql.BillingOverageCharges.Delete, charge),
        SkippedBillingMeter skipped => connection.ExecuteAsync(DashboardSql.BillingSkippedMeters.Delete, skipped),
        StripeEventReceipt receipt => connection.ExecuteAsync(DashboardSql.StripeEventReceipts.Delete, receipt),
        StripeCustomerRow customer => connection.ExecuteAsync(DashboardSql.StripeCustomers.Delete, customer),
        StripeCheckoutRow checkout => connection.ExecuteAsync(DashboardSql.StripeCheckouts.Delete, checkout),
        PendingOrganizationCheckout pending => connection.ExecuteAsync(DashboardSql.PendingOrganizationCheckouts.Delete, pending),
        InternalBillingRow internalBilling => connection.ExecuteAsync(DashboardSql.InternalBilling.Delete, internalBilling),
        _ => throw new UnreachableException(string.Concat("Billing storage has no delete for ", row.GetType().Name, ".")),
    };
}
