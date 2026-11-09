using TerraScale.TerraBase.Contracts;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StripeException = Stripe.StripeException;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed class UsageOverageReporter(IDbContextFactory<DashboardDbContext> storage, OrganizationStore organizations, IAllowanceNotificationBackend usage, StripeBilling billing, TimeProvider clock, ILogger<UsageOverageReporter> logger)
    {
        private const int ClosingDays = 3;

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            if (clock.GetUtcNow().Day <= ClosingDays)
            {
                await RunMonthAsync(true, cancellationToken);
            }

            await RunMonthAsync(false, cancellationToken);
        }

        private async Task RunMonthAsync(bool previous, CancellationToken cancellationToken)
        {
            string? after = null;
            do
            {
                var page = await usage.ReadAsync(after, previous, cancellationToken);
                var month = AllowanceNotifications.ValidatedMonth(page, after, clock.GetUtcNow(), previous);
                foreach (var item in page.Items)
                {
                    if (item.Operations is not { } units)
                    {
                        continue;
                    }

                    var byteHours = page.StorageContract == AllowanceNotifications.StorageContract ? item.StorageByteHours : null;
                    try { await ReportAsync(item.OrganizationId, units, byteHours, month, cancellationToken); }
                    catch (Exception error) when (error is TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or StripeException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                    {
                        logger.UsageOverageCouldNotBeReported(error.GetType().Name);
                    }
                }
                after = page.Next;
            } while (after is not null);
        }

        public async Task ReportAsync(string organizationId, long units, long? storageByteHours, DateTimeOffset month, CancellationToken cancellationToken)
        {
            var row = await organizations.FindAsync(organizationId, cancellationToken);
            if (row is null)
            {
                return;
            }

            var organization = row.Erased ? throw new IOException("Dashboard record was erased.") : row.Organization;
            // Internal billing reports usage without Stripe charges.
            if (organization.InternalBilling is not null || PlanCatalog.For(organization) is not { PriceCents: > 0 } plan)
            {
                return;
            }

            var bindingRow = await storage.BindingRowAsync(organizationId, cancellationToken);
            if (bindingRow is null)
            {
                return;
            }

            var binding = StripeBilling.ReadBinding(bindingRow, organizationId);
            if (binding.Kind != "stripe" || binding.CustomerId.Length == 0 || binding.SubscriptionId.Length == 0 || binding.Status is not ("active" or "trialing" or "past_due"))
            {
                return;
            }

            await billing.ReportOverageAsync(binding, StripeBilling.OperationUnits(plan), OperationUnitOverage.Units(plan, units), month, cancellationToken);
            if (storageByteHours is { } byteHours)
            {
                await billing.ReportOverageAsync(binding, StripeBilling.Storage(plan), StorageOverage.MegabyteMonths(plan, byteHours, month), month, cancellationToken);
            }
        }
    }

    public sealed class UsageOverageWorker(UsageOverageReporter reporter, TimeProvider clock, ILogger<UsageOverageWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
            do
            {
                try { await reporter.RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or StripeException or OperationCanceledException)
                {
                    logger.UsageOverageReportingIsUnavailableRetrying(error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }
}
