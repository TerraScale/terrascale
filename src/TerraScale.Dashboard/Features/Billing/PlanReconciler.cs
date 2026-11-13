using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Features.Billing
{
    // Converges TerraBase to every organization's entitled plan every 30 seconds, whatever was missed: a TerraBase wipe or
    // re-seed, an enrollment after the last billing change, a failed publish, or a paid checkout whose webhook never came.
    public sealed class PlanReconciler(IDbContextFactory<DashboardDbContext> storage, OrganizationStore organizations, StripeBilling billing, TerraBasePlanSync plans, TimeProvider clock, ILogger<PlanReconciler> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
            do
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is DbUpdateException or TimeoutException or TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
                {
                    logger.PlanReconciliationIsUnavailableRetryingIn(error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            await billing.RecoverEventReceiptsAsync(cancellationToken);
            List<BillingBinding> pending;
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                pending = await db.Billing.AsNoTracking().Where(row => row.ErasureId == null && row.Kind == "stripe" && row.CheckoutId != "" && row.SubscriptionId == "" && row.Status == "pending")
                    .ToListAsync(cancellationToken);
            }

            foreach (var binding in pending)
            {
                try
                {
                    await billing.ReconcilePendingCheckoutAsync(binding, cancellationToken);
                }
                catch (Exception error) when (error is DbUpdateException or TimeoutException or TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or Stripe.StripeException)
                {
                    logger.APendingCheckoutDidNotReconcile(error.GetType().Name);
                }
            }
            foreach (var organization in await organizations.ListAsync(cancellationToken))
            {
                var current = organization;
                if (organization.BillingStatus == "active" && organization.PaymentGraceUntil is { } deadline && deadline <= clock.GetUtcNow())
                {
                    current = await billing.ResolveExpiredGraceAsync(organization.Id, cancellationToken);
                }
                await plans.PublishAsync(current, reconcile: true, cancellationToken);
            }
        }
    }
}
