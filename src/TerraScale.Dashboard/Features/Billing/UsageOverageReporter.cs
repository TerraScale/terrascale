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
        public Task RunCurrentAsync(CancellationToken cancellationToken) => RunMonthAsync(null, cancellationToken);

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            try { await RunCurrentAsync(cancellationToken); }
            catch (Exception error) when (IsReportingFailure(error, cancellationToken))
            {
                logger.UsageOverageReportingIsUnavailableRetrying(error.GetType().Name);
            }
            var now = clock.GetUtcNow();
            var current = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var bindings = await db.Billing.AsNoTracking()
                .Where(row => row.Kind == "stripe" && row.State != "deleting" && row.SubscriptionId != "" && row.ErasureId == null &&
                    (row.Status == "active" || row.Status == "trialing" || row.Status == "past_due") &&
                    (row.EffectivePlan == "starter" || row.EffectivePlan == "pro" || row.EffectivePlan == "team"))
                .ToListAsync(cancellationToken);
            DateTimeOffset? earliest = null;
            foreach (var binding in bindings)
            {
                try
                {
                    var currentBinding = binding;
                    while (!await BillingMonth.RecordAsync(storage, currentBinding, now, cancellationToken))
                    {
                        currentBinding = await storage.BindingRowAsync(binding.OrganizationId, cancellationToken) ?? throw new IOException("Billing state is unavailable.");
                    }
                }
                catch (Exception error) when (IsReportingFailure(error, cancellationToken))
                {
                    logger.UsageOverageCouldNotBeReported(error.GetType().Name);
                }

                var first = binding.OverageReportedThrough?.AddMonths(1) ?? new DateTimeOffset(binding.StartedAt.Year, binding.StartedAt.Month, 1, 0, 0, 0, TimeSpan.Zero);
                var started = new DateTimeOffset(binding.StartedAt.Year, binding.StartedAt.Month, 1, 0, 0, 0, TimeSpan.Zero);
                first = first < started ? started : first;
                earliest = earliest is null || first < earliest ? first : earliest;
            }

            if (earliest is { } start)
            {
                for (var month = start; month < current; month = month.AddMonths(1))
                {
                    try { await RunMonthAsync(month, cancellationToken); }
                    catch (Exception error) when (IsReportingFailure(error, cancellationToken))
                    {
                        logger.UsageOverageReportingIsUnavailableRetrying(error.GetType().Name);
                    }
                }
            }
        }

        private static bool IsReportingFailure(Exception error, CancellationToken cancellationToken) =>
            error is TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or StripeException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested;

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
                    catch (Exception error) when (IsReportingFailure(error, cancellationToken))
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

            var complete = month.AddMonths(1) <= clock.GetUtcNow();
            if (complete)
            {
                await using var db = await storage.CreateDbContextAsync(cancellationToken);
                var recorded = await db.BillingMonths.AsNoTracking().SingleOrDefaultAsync(item => item.OrganizationId == organizationId && item.Month == month, cancellationToken);
                if (recorded is null || recorded.SubscriptionId != binding.SubscriptionId)
                {
                    billing.RecordSkippedOverageMonth(organizationId, month, "missing_plan");
                    await AdvanceAsync(binding, month, firstMonth, cancellationToken);
                    return;
                }

                plan = PlanCatalog.For(recorded.Plan);
            }

            if (storageByteHours is not { } byteHours)
            {
                if (complete)
                {
                    billing.RecordSkippedOverageMonth(organizationId, month, "missing_storage_contract");
                    await AdvanceAsync(binding, month, firstMonth, cancellationToken);
                }
                return;
            }

            await billing.ReportOverageAsync(binding, StripeBilling.OperationUnits(plan), OperationUnitOverage.Units(plan, units), month, cancellationToken);
            await billing.ReportOverageAsync(binding, StripeBilling.Storage(plan), StorageOverage.MegabyteMonths(plan, byteHours, month), month, cancellationToken);
            if (complete)
            {
                await AdvanceAsync(binding, month, firstMonth, cancellationToken);
            }
        }

        private async Task AdvanceAsync(BillingBinding binding, DateTimeOffset month, DateTimeOffset firstMonth, CancellationToken cancellationToken)
        {
            var organizationId = binding.OrganizationId;
            while (true)
            {
                var fresh = await storage.BindingRowAsync(organizationId, cancellationToken) ?? throw new IOException("Billing state is unavailable.");
                if (fresh.OverageReportedThrough >= month)
                {
                    return;
                }

                var expected = fresh.OverageReportedThrough?.AddMonths(1) ?? firstMonth;
                expected = expected < firstMonth ? firstMonth : expected;
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
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
            DateOnly? lastDaily = null;
            do
            {
                try
                {
                    var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
                    if (lastDaily != today)
                    {
                        await reporter.RunOnceAsync(stoppingToken);
                        lastDaily = today;
                    }
                    else
                    {
                        await reporter.RunCurrentAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or StripeException or OperationCanceledException)
                {
                    logger.UsageOverageReportingIsUnavailableRetrying(error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }
}
