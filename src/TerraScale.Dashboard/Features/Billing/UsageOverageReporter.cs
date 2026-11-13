using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StripeException = Stripe.StripeException;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.TerraBase.Contracts;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed class UsageOverageReporter(IDbContextFactory<DashboardDbContext> storage, IAllowanceNotificationBackend usage, StripeBilling billing, TimeProvider clock, ILogger<UsageOverageReporter> logger)
    {
        public async Task<long> OperationUnitOverageAsync(BillingBinding binding, CancellationToken cancellationToken)
        {
            string? after = null;
            do
            {
                var page = await usage.ReadAsync(after, null, cancellationToken);
                AllowanceNotifications.ValidatedMonth(page, after, clock.GetUtcNow(), null);
                var item = page.Items.FirstOrDefault(item => item.OrganizationId == binding.OrganizationId);
                if (item is not null)
                {
                    var units = item.Operations ?? throw new IOException("Operation usage is unavailable.");
                    return OperationUnitOverage.Units(PlanCatalog.For(binding.EffectivePlan), units);
                }
                after = page.Next;
            } while (after is not null);
            return 0;
        }

        public async Task RunCurrentAsync(CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var bindings = await db.Billing.AsNoTracking().Where(row => row.Kind == "stripe" && row.State != "deleting" && row.SubscriptionId != "" && row.ErasureId == null).ToListAsync(cancellationToken);
            foreach (var binding in bindings)
            {
                try
                {
                    var fresh = binding;
                    while (!await BillingMonth.RecordAsync(storage, fresh, clock.GetUtcNow(), cancellationToken))
                    {
                        fresh = await storage.BindingRowAsync(binding.OrganizationId, cancellationToken) ?? throw new IOException("Billing state is unavailable.");
                    }
                }
                catch (Exception error) when (IsReportingFailure(error, cancellationToken))
                {
                    logger.UsageOverageCouldNotBeReported(error.GetType().Name);
                }
            }
            var pending = await db.OverageCharges.AsNoTracking().Where(row => row.ProviderId == null && row.SkipReason == null && row.AmountCents > 0).ToListAsync(cancellationToken);
            foreach (var charge in pending)
            {
                try { await billing.ReportOverageAsync(charge, cancellationToken); }
                catch (Exception error) when (IsReportingFailure(error, cancellationToken))
                {
                    logger.UsageOverageCouldNotBeReported(error.GetType().Name);
                }
            }
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            await RunCurrentAsync(cancellationToken);
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var starts = await db.Billing.AsNoTracking().Where(row => row.Kind == "stripe" && row.SubscriptionId != "" && row.State != "deleting" && row.ErasureId == null && row.OverageStartsAt != default).Select(row => row.OverageStartsAt).ToListAsync(cancellationToken);
            var now = clock.GetUtcNow();
            var current = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var earliest = starts.Count > 0 ? starts.Min() : current;
            var months = await db.BillingMonths.AsNoTracking().Where(row => row.Month >= earliest && row.Month < current).Select(row => row.Month)
                .Union(db.BillingTermObservations.Where(row => row.Month >= earliest && row.Month < current).Select(row => row.Month))
                .Distinct().OrderBy(month => month).ToListAsync(cancellationToken);
            foreach (var month in months)
            {
                try { await RunMonthAsync(month, cancellationToken); }
                catch (Exception error) when (IsReportingFailure(error, cancellationToken))
                {
                    logger.UsageOverageReportingIsUnavailableRetrying(error.GetType().Name);
                }
            }
        }

        private static bool IsReportingFailure(Exception error, CancellationToken cancellationToken) =>
            error is DbUpdateException or TimeoutException or TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or StripeException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested;

        private async Task RunMonthAsync(DateTimeOffset requestedMonth, CancellationToken cancellationToken)
        {
            string? after = null;
            do
            {
                var page = await usage.ReadAsync(after, requestedMonth, cancellationToken);
                var month = AllowanceNotifications.ValidatedMonth(page, after, clock.GetUtcNow(), requestedMonth);
                foreach (var item in page.Items)
                {
                    if (item.Operations is not { } units) { continue; }
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
            if (month.Offset != TimeSpan.Zero || month.Day != 1 || month.TimeOfDay != TimeSpan.Zero || month.AddMonths(1) > clock.GetUtcNow()) { return; }
            ArgumentOutOfRangeException.ThrowIfNegative(units);
            var binding = await storage.BindingRowAsync(organizationId, cancellationToken);
            if (binding is null || binding.State == "deleting" || binding.ErasureId is not null || binding.Kind != "stripe" || binding.CustomerId.Length == 0 || binding.SubscriptionId.Length == 0 || binding.OverageStartsAt == default || month < binding.OverageStartsAt) { return; }
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            if (await db.SkippedBillingMonths.AnyAsync(row => row.OrganizationId == organizationId && row.SubscriptionId == binding.SubscriptionId && row.Month == month, cancellationToken)) { return; }
            var baseline = await db.BillingMonths.AsNoTracking().SingleOrDefaultAsync(row => row.OrganizationId == organizationId && row.SubscriptionId == binding.SubscriptionId && row.Month == month, cancellationToken);
            var end = month.AddMonths(1);
            var observations = await db.BillingTermObservations.AsNoTracking().Where(row => row.OrganizationId == organizationId && row.SubscriptionId == binding.SubscriptionId && row.ObservedAt < end).ToListAsync(cancellationToken);
            var starting = observations.Where(row => row.ObservedAt < month).Select(row => (DateTimeOffset?)row.ObservedAt).Max();
            var relevant = observations.Where(row => row.ObservedAt >= month || row.ObservedAt == starting).ToList();
            var unknownTerms = relevant.Any(row => row.PricingVersion.Length == 0);
            var candidates = relevant.Where(row => row.PricingVersion.Length > 0).Select(row => row.TermsFor(month)).ToList();
            if (baseline is not null) { candidates.Add(baseline); }
            if (candidates.Count == 0 || storageByteHours is null)
            {
                var reason = binding.Status is "canceled" or "incomplete_expired" ? "subscription_ended" : candidates.Count == 0 ? "missing_plan" : "missing_storage_contract";
                if (await storage.InsertAsync(new SkippedBillingMonth { OrganizationId = organizationId, SubscriptionId = binding.SubscriptionId, Month = month, Reason = reason }, cancellationToken))
                {
                    billing.RecordSkippedOverageMonth(month, reason);
                }
                return;
            }
            ArgumentOutOfRangeException.ThrowIfNegative(storageByteHours.Value);
            var hours = (long)(month.AddMonths(1) - month).TotalHours;
            var skipReason = binding.Status is "canceled" or "incomplete_expired" ? "subscription_ended" : unknownTerms ? "missing_plan" : null;
            var operations = await FreezeLowestAsync(binding, candidates, OperationUnitOverage.Kind, units, hours, skipReason, cancellationToken);
            var stored = await FreezeLowestAsync(binding, candidates, StorageOverage.Kind, storageByteHours.Value, hours, skipReason, cancellationToken);
            foreach (var charge in new[] { operations, stored })
            {
                if (charge.AmountCents > 0 && charge.ProviderId is null && charge.SkipReason is null)
                {
                    try { await billing.ReportOverageAsync(charge, cancellationToken); }
                    catch (Exception error) when (IsReportingFailure(error, cancellationToken))
                    {
                        logger.UsageOverageCouldNotBeReported(error.GetType().Name);
                    }
                }
            }
        }

        private async Task<OverageCharge> FreezeLowestAsync(BillingBinding binding, IReadOnlyList<BillingMonth> candidates, string meter, long used, long hours, string? skipReason, CancellationToken cancellationToken)
        {
            var priced = candidates.Select(terms =>
            {
                var allowance = meter == OperationUnitOverage.Kind ? terms.OperationsAllowance : checked(terms.StorageAllowance * hours);
                var quantity = meter == OperationUnitOverage.Kind ? Math.Max(0, used - allowance) : (long)BigInteger.Max(0, ((BigInteger)used - allowance) / hours / 1_000_000);
                var rate = meter == OperationUnitOverage.Kind ? terms.OperationRate : terms.StorageRate;
                var amount = checked((long)Math.Round(quantity * rate, 0, MidpointRounding.AwayFromZero));
                return (Terms: terms, Allowance: allowance, Quantity: quantity, Rate: rate, Amount: amount);
            }).OrderBy(item => item.Amount).ThenBy(item => item.Terms.PricingVersion, StringComparer.Ordinal).First();
            var (terms, allowance, quantity, rate, amount) = priced;
            var unitName = meter == OperationUnitOverage.Kind ? "Operation units" : "Storage MB-months";
            var charge = new OverageCharge
            {
                Id = AccountService.NewId(),
                OrganizationId = binding.OrganizationId,
                SubscriptionId = binding.SubscriptionId,
                CustomerId = binding.CustomerId,
                Month = terms.Month,
                Meter = meter,
                FrozenAt = clock.GetUtcNow(),
                Usage = used,
                Allowance = allowance,
                UnitRate = rate,
                Currency = terms.Currency,
                PricingVersion = terms.PricingVersion,
                PlanName = terms.PlanName,
                Quantity = quantity,
                AmountCents = amount,
                SkipReason = skipReason,
                Description = string.Create(CultureInfo.InvariantCulture, $"{unitName} overage, {terms.PlanName}, {terms.Month:yyyy-MM}: {quantity:N0}")
            };
            if (await storage.InsertAsync(charge, cancellationToken)) { return charge; }
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var winner = await db.OverageCharges.AsNoTracking().SingleAsync(row => row.OrganizationId == binding.OrganizationId && row.SubscriptionId == binding.SubscriptionId && row.Month == terms.Month && row.Meter == meter, cancellationToken);
            if (used > winner.Usage) { billing.RecordLateOverage(); }
            return winner;
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
                catch (Exception error) when (error is DbUpdateException or TimeoutException or TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or StripeException or OperationCanceledException)
                {
                    logger.UsageOverageReportingIsUnavailableRetrying(error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }
}
