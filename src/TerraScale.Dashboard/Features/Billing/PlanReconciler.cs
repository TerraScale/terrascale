using Dapper;
using System.Data;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Clients;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed class PlanReconciler(DashboardDb storage, OrganizationStore organizations, StripeBilling billing, BillingSchemaGate gate, TerraBasePlanSync plans, TimeProvider clock, ILogger<PlanReconciler> logger, BackgroundJobs jobs) : BackgroundService
    {
        private const string PendingWhere = " WHERE erasure_id IS NULL AND kind = 'stripe' AND checkout_id <> '' AND subscription_id = '' AND status = 'pending'";
        private const string CheckoutsDue = "SELECT count(*) FROM billing" + PendingWhere;
        private const string PendingCheckouts = DashboardSql.Billing.Select + PendingWhere;
        private const string ReceiptsDue = "SELECT count(*) FROM stripe_event_receipts WHERE completed_at IS NULL AND processing_until <= @Now";

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
            do
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is DBConcurrencyException or TimeoutException or TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
                {
                    logger.PlanReconciliationIsUnavailableRetryingIn(error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            if (!await gate.AllowsAsync(cancellationToken)) { return; }
            var candidates = await organizations.ListAsync(cancellationToken);
            var now = clock.GetUtcNow();
            bool checkoutDue;
            bool receiptDue;
            await using (var snapshot = await storage.OpenAsync(cancellationToken))
            {
                checkoutDue = await snapshot.ExecuteScalarAsync<long>(CheckoutsDue) > 0;
                receiptDue = await snapshot.ExecuteScalarAsync<long>(ReceiptsDue, new { Now = now }) > 0;
            }

            if (!checkoutDue && !receiptDue && !candidates.Any(row => row.BillingStatus == "active" && row.PaymentGraceUntil <= now) &&
                !await plans.AnyDueAsync(candidates, cancellationToken)) { return; }
            await jobs.RunAsync("plan-reconciliation", TimeSpan.FromSeconds(30), async (claim, token) =>
            {
                await billing.RecoverEventReceiptsAsync(token);
                List<BillingBinding> pending;
                await using (var connection = await storage.OpenAsync(token))
                {
                    pending = (await connection.QueryAsync<BillingBinding>(PendingCheckouts)).AsList();
                }

                foreach (var binding in pending)
                {
                    try
                    {
                        await billing.ReconcilePendingCheckoutAsync(binding, token);
                    }
                    catch (Exception error) when (error is DBConcurrencyException or TimeoutException or TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or Stripe.StripeException)
                    {
                        logger.APendingCheckoutDidNotReconcile(error.GetType().Name);
                    }
                }
                foreach (var organization in await organizations.ListAsync(token))
                {
                    var current = organization;
                    if (organization.BillingStatus == "active" && organization.PaymentGraceUntil is { } deadline && deadline <= clock.GetUtcNow())
                    {
                        current = await billing.ResolveExpiredGraceAsync(organization.Id, token);
                    }
                    await plans.PublishAsync(current, reconcile: true, token);
                }
            }, cancellationToken);
        }
    }
}
