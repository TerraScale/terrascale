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
        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            var current = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var earliest = await db.Billing.AsNoTracking()
                .Where(row => row.Kind == "stripe" && row.State != "deleting" && row.SubscriptionId != "" && row.ErasureId == null)
                .OrderBy(row => row.OverageReportedThrough ?? row.StartedAt)
                .Select(row => (DateTimeOffset?)(row.OverageReportedThrough ?? row.StartedAt)).FirstOrDefaultAsync(cancellationToken);
            if (earliest is { } start)
            {
                for (var month = new DateTimeOffset(start.Year, start.Month, 1, 0, 0, 0, TimeSpan.Zero); month < current; month = month.AddMonths(1))
                {
                    await RunMonthAsync(month, cancellationToken);
                }
            }

            await RunMonthAsync(null, cancellationToken);
        }

        private async Task RunMonthAsync(DateTimeOffset? requestedMonth, CancellationToken cancellationToken)
        {
            string? after = null;
            do
            {
                var page = await usage.ReadAsync(after, requestedMonth, cancellationToken);
                var month = AllowanceNotifications.ValidatedMonth(page, after, clock.GetUtcNow(), requestedMonth);
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
            if (binding.State == "deleting" || binding.Kind != "stripe" || binding.CustomerId.Length == 0 || binding.SubscriptionId.Length == 0 || binding.Status is not ("active" or "trialing" or "past_due"))
            {
                return;
            }

            var firstMonth = new DateTimeOffset(binding.StartedAt.Year, binding.StartedAt.Month, 1, 0, 0, 0, TimeSpan.Zero);
            if (month < firstMonth || binding.OverageReportedThrough >= month)
            {
                return;
            }

            await billing.ReportOverageAsync(binding, StripeBilling.OperationUnits(plan), OperationUnitOverage.Units(plan, units), month, cancellationToken);
            if (storageByteHours is { } byteHours)
            {
                await billing.ReportOverageAsync(binding, StripeBilling.Storage(plan), StorageOverage.MegabyteMonths(plan, byteHours, month), month, cancellationToken);
            }
            else
            {
                throw new IOException("Storage metering is unavailable; the overage month remains pending.");
            }

            if (month.AddMonths(1) > clock.GetUtcNow())
            {
                return;
            }

            while (true)
            {
                var fresh = await storage.BindingRowAsync(organizationId, cancellationToken) ?? throw new IOException("Billing state is unavailable.");
                if (fresh.OverageReportedThrough >= month)
                {
                    return;
                }

                var expected = fresh.OverageReportedThrough?.AddMonths(1) ?? firstMonth;
                if (expected != month || fresh.SubscriptionId != binding.SubscriptionId || fresh.State == "deleting")
                {
                    return;
                }

                fresh.OverageReportedThrough = month;
                if (await storage.ReplaceAsync(fresh, cancellationToken))
                {
                    return;
                }
            }
        }
    }

    public sealed class UsageOverageWorker(UsageOverageReporter reporter, TimeProvider clock, ILogger<UsageOverageWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromDays(1), clock);
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
