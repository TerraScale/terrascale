using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed class StripeBindingRefresh(IDbContextFactory<DashboardDbContext> storage, StripeBilling billing, TimeProvider clock, ILogger<StripeBindingRefresh> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromDays(1), clock);
            do
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
                {
                    logger.StripeBindingRefreshIsUnavailableRetryingNext(error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            List<string> organizationIds;
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                organizationIds = await db.Billing.AsNoTracking()
                    .Where(row => row.ErasureId == null && row.Kind == "stripe" && (row.SubscriptionId != "" || row.CheckoutId != ""))
                    .Select(row => row.OrganizationId)
                    .ToListAsync(cancellationToken);
            }

            foreach (var organizationId in organizationIds)
            {
                try
                {
                    await billing.RefreshAsync(organizationId, cancellationToken);
                }
                catch (Exception error) when (error is TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or Stripe.StripeException)
                {
                    logger.AStripeBindingCouldNotBeRefreshed(error.GetType().Name);
                }
            }
        }
    }
}
