using System.Collections.Concurrent;
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
    public enum MonthSettlement
    {
        NotDue,
        WaitingForTerms,
        Settled
    }

    public sealed class UsageOverageReporter(IDbContextFactory<DashboardDbContext> storage, IAllowanceNotificationBackend usage, StripeBilling billing, BillingSchemaGate gate, TimeProvider clock, ILogger<UsageOverageReporter> logger)
    {
        private const int MonthsPerPass = 3;
        private const int UsagePagesPerPass = 200;
        private const int DispatchesPerPass = 500;
        private const int PageSize = 200;
        private readonly ConcurrentDictionary<DateTimeOffset, string> resume = new();

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
            if (!await gate.AllowsAsync(cancellationToken)) { return; }
            await DispatchPendingAsync(cancellationToken);
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            if (!await gate.AllowsAsync(cancellationToken)) { return; }
            await SettleMonthsAsync(cancellationToken);
            await DispatchPendingAsync(cancellationToken);
        }

        private async Task DispatchPendingAsync(CancellationToken cancellationToken)
        {
            string? after = null;
            var dispatched = 0;
            while (dispatched < DispatchesPerPass)
            {
                List<string> page;
                await using (var db = await storage.CreateDbContextAsync(cancellationToken))
                {
                    page = await db.OverageCharges.AsNoTracking().Where(row => row.State == OverageCharge.Pending && (after == null || string.Compare(row.Id, after) > 0))
                        .OrderBy(row => row.Id).Select(row => row.Id).Take(PageSize).ToListAsync(cancellationToken);
                }
                foreach (var id in page.Take(DispatchesPerPass - dispatched))
                {
                    dispatched++;
                    try { await billing.DispatchOverageAsync(id, cancellationToken); }
                    catch (Exception error) when (IsReportingFailure(error, cancellationToken))
                    {
                        logger.UsageOverageCouldNotBeReported(error.GetType().Name);
                    }
                }
                if (page.Count < PageSize) { return; }
                after = page[^1];
            }
        }

        private async Task SettleMonthsAsync(CancellationToken cancellationToken)
        {
            var current = MonthOf(clock.GetUtcNow());
            List<DateTimeOffset> months;
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                months = await db.Billing.AsNoTracking().Where(row => row.Kind == "stripe" && row.State != "deleting" && row.ErasureId == null && row.OverageStartsAt != default && row.OverageStartsAt < current)
                    .Select(row => row.OverageStartsAt).Distinct().OrderBy(month => month).Take(MonthsPerPass).ToListAsync(cancellationToken);
            }
            var pages = 0;
            long waiting = 0;
            DateTimeOffset? oldest = null;
            foreach (var month in months)
            {
                string? after = resume.TryGetValue(month, out var resumed) ? resumed : null;
                while (pages < UsagePagesPerPass)
                {
                    AllowanceUsagePage page;
                    try
                    {
                        page = await usage.ReadAsync(after, month, cancellationToken);
                        AllowanceNotifications.ValidatedMonth(page, after, clock.GetUtcNow(), month);
                    }
                    catch (Exception error) when (IsReportingFailure(error, cancellationToken))
                    {
                        logger.UsageOverageReportingIsUnavailableRetrying(error.GetType().Name);
                        break;
                    }
                    pages++;
                    var measured = page.Items.ToDictionary(item => item.OrganizationId, StringComparer.Ordinal);
                    var storageKnown = page.StorageContract == AllowanceNotifications.StorageContract;
                    string? organizationAfter = after;
                    while (true)
                    {
                        List<BillingBinding> bindings;
                        await using (var db = await storage.CreateDbContextAsync(cancellationToken))
                        {
                            var upper = page.Next;
                            bindings = await db.Billing.AsNoTracking().Where(row => row.Kind == "stripe" && row.OverageStartsAt == month &&
                                    (organizationAfter == null || string.Compare(row.OrganizationId, organizationAfter) > 0) && (upper == null || string.Compare(row.OrganizationId, upper) <= 0))
                                .OrderBy(row => row.OrganizationId).Take(PageSize).ToListAsync(cancellationToken);
                        }
                        foreach (var binding in bindings)
                        {
                            var item = measured.GetValueOrDefault(binding.OrganizationId);
                            var units = item is null ? 0 : item.Operations;
                            var byteHours = storageKnown ? item is null ? 0 : item.StorageByteHours : null;
                            try
                            {
                                if (await SettleAsync(binding, month, units, byteHours, cancellationToken) == MonthSettlement.WaitingForTerms)
                                {
                                    waiting++;
                                    oldest = oldest is { } known && known < month ? known : month;
                                }
                            }
                            catch (Exception error) when (IsReportingFailure(error, cancellationToken))
                            {
                                logger.UsageOverageCouldNotBeReported(error.GetType().Name);
                            }
                        }
                        if (bindings.Count < PageSize) { break; }
                        organizationAfter = bindings[^1].OrganizationId;
                    }
                    after = page.Next;
                    if (after is null)
                    {
                        resume.TryRemove(month, out _);
                        break;
                    }
                    resume[month] = after;
                }
            }
            billing.RecordWaitingOverageMonths(waiting, oldest);
        }

        private static bool IsReportingFailure(Exception error, CancellationToken cancellationToken) =>
            error is DbUpdateException or TimeoutException or TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or StripeException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested;

        private static DateTimeOffset MonthOf(DateTimeOffset value) => new(value.Year, value.Month, 1, 0, 0, 0, TimeSpan.Zero);

        public async Task<MonthSettlement> ReportAsync(string organizationId, long? units, long? storageByteHours, DateTimeOffset month, CancellationToken cancellationToken)
        {
            var binding = await storage.BindingRowAsync(organizationId, cancellationToken);
            return binding is null ? MonthSettlement.NotDue : await SettleAsync(binding, month, units, storageByteHours, cancellationToken);
        }

        private async Task<MonthSettlement> SettleAsync(BillingBinding binding, DateTimeOffset month, long? units, long? storageByteHours, CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            var end = month.AddMonths(1);
            if (month != MonthOf(month) || end > now || binding.Kind != "stripe" || binding.State == "deleting" || binding.ErasureId is not null || binding.OverageStartsAt != month)
            {
                return MonthSettlement.NotDue;
            }
            List<BillingSubscription> subscriptions;
            List<BillingTerms> history;
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                subscriptions = await db.BillingSubscriptions.AsNoTracking().Where(row => row.OrganizationId == binding.OrganizationId).ToListAsync(cancellationToken);
                history = await db.BillingTerms.AsNoTracking().Where(row => row.OrganizationId == binding.OrganizationId && row.EffectiveFrom < end).ToListAsync(cancellationToken);
            }
            var overlapping = subscriptions.Where(row => row.StartedAt < end && (row.EndedAt is null || row.EndedAt > month)).ToList();
            if (binding.SubscriptionId.Length > 0 && subscriptions.All(row => row.SubscriptionId != binding.SubscriptionId) || overlapping.Any(row => !row.KnownThrough(end)))
            {
                return MonthSettlement.WaitingForTerms;
            }
            if (overlapping.Count == 0)
            {
                var next = subscriptions.Where(row => row.StartedAt >= end).Select(row => MonthOf(row.StartedAt)).DefaultIfEmpty(MonthOf(now)).Min();
                await AdvanceAsync(binding.OrganizationId, month, next < end ? end : next, cancellationToken);
                return MonthSettlement.Settled;
            }
            var (candidates, reason) = Candidates(overlapping, history, month, end);
            var owner = overlapping.MaxBy(row => row.StartedAt)!;
            var hours = (long)(end - month).TotalHours;
            var operations = await FreezeAsync(binding, owner, month, OperationUnitOverage.Kind, units, hours, candidates, reason, "missing_operations", cancellationToken);
            var stored = await FreezeAsync(binding, owner, month, StorageOverage.Kind, storageByteHours, hours, candidates, reason, "missing_storage_contract", cancellationToken);
            await AdvanceAsync(binding.OrganizationId, month, end, cancellationToken);
            foreach (var charge in new[] { operations, stored })
            {
                if (charge is { State: OverageCharge.Pending })
                {
                    try { await billing.DispatchOverageAsync(charge.Id, cancellationToken); }
                    catch (Exception error) when (IsReportingFailure(error, cancellationToken))
                    {
                        logger.UsageOverageCouldNotBeReported(error.GetType().Name);
                    }
                }
            }
            return MonthSettlement.Settled;
        }

        private static (IReadOnlyList<BillingTerms> Candidates, string? Reason) Candidates(IReadOnlyList<BillingSubscription> overlapping, IReadOnlyList<BillingTerms> history, DateTimeOffset month, DateTimeOffset end)
        {
            var candidates = new List<BillingTerms>();
            string? reason = null;
            foreach (var subscription in overlapping)
            {
                var own = history.Where(row => row.SubscriptionId == subscription.SubscriptionId).OrderBy(row => row.EffectiveFrom).ToList();
                var from = subscription.StartedAt > month ? subscription.StartedAt : month;
                var until = subscription.EndedAt is { } ended && ended < end ? ended : end;
                if (own.LastOrDefault(row => row.EffectiveFrom <= from) is { } starting) { candidates.Add(starting); }
                else { reason = "terms_unestablished"; }
                candidates.AddRange(own.Where(row => row.EffectiveFrom > from && row.EffectiveFrom < until));
            }
            if (candidates.Any(row => row.Basis == BillingTerms.Unestablished)) { reason = "terms_unestablished"; }
            else if (reason is null && candidates.Any(row => !row.Priced)) { reason = "missing_plan"; }
            return (candidates.Where(row => row.Priced).ToList(), reason);
        }

        private async Task<OverageCharge?> FreezeAsync(BillingBinding binding, BillingSubscription owner, DateTimeOffset month, string meter, long? used, long hours, IReadOnlyList<BillingTerms> candidates, string? reason, string missingReason, CancellationToken cancellationToken)
        {
            if (used is null || candidates.Count == 0)
            {
                var skip = used is null ? missingReason : reason ?? "missing_plan";
                if (await storage.InsertAsync(new SkippedBillingMeter { OrganizationId = binding.OrganizationId, Month = month, Meter = meter, Reason = skip }, cancellationToken))
                {
                    billing.RecordSkippedOverageMeter(month, skip);
                }
                return null;
            }
            ArgumentOutOfRangeException.ThrowIfNegative(used.Value);
            var priced = candidates.Select(terms =>
            {
                var allowance = meter == OperationUnitOverage.Kind ? terms.OperationsAllowance : checked(terms.StorageAllowance * hours);
                var quantity = meter == OperationUnitOverage.Kind ? Math.Max(0, used.Value - allowance) : (long)BigInteger.Max(0, ((BigInteger)used.Value - allowance) / hours / 1_000_000);
                var rate = meter == OperationUnitOverage.Kind ? terms.OperationRate : terms.StorageRate;
                var amount = checked((long)Math.Round(quantity * rate, 0, MidpointRounding.AwayFromZero));
                return (Terms: terms, Allowance: allowance, Quantity: quantity, Rate: rate, Amount: amount);
            }).OrderBy(item => item.Amount).ThenBy(item => item.Terms.PricingVersion, StringComparer.Ordinal).First();
            var unitName = meter == OperationUnitOverage.Kind ? "Operation units" : "Storage MB-months";
            var charge = new OverageCharge
            {
                Id = AccountService.NewId(),
                OrganizationId = binding.OrganizationId,
                SubscriptionId = owner.SubscriptionId,
                CustomerId = owner.CustomerId,
                Month = month,
                Meter = meter,
                FrozenAt = clock.GetUtcNow(),
                Usage = used.Value,
                Allowance = priced.Allowance,
                UnitRate = priced.Rate,
                Currency = priced.Terms.Currency,
                PricingVersion = priced.Terms.PricingVersion,
                PlanName = priced.Terms.PlanName,
                Quantity = priced.Quantity,
                AmountCents = priced.Amount,
                State = priced.Amount == 0 ? OverageCharge.NothingDue : reason is null ? OverageCharge.Pending : OverageCharge.Skipped,
                SkipReason = priced.Amount == 0 ? null : reason,
                Description = string.Create(CultureInfo.InvariantCulture, $"{unitName} overage, {priced.Terms.PlanName}, {month:yyyy-MM}: {priced.Quantity:N0}")
            };
            if (await storage.InsertAsync(charge, cancellationToken))
            {
                if (charge.SkipReason is { } skipped) { billing.RecordSkippedOverageMeter(month, skipped); }
                return charge;
            }
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var winner = await db.OverageCharges.AsNoTracking().SingleAsync(row => row.OrganizationId == binding.OrganizationId && row.Month == month && row.Meter == meter, cancellationToken);
            if (used.Value > winner.Usage) { billing.RecordLateOverage(); }
            return winner;
        }

        private async Task AdvanceAsync(string organizationId, DateTimeOffset month, DateTimeOffset next, CancellationToken cancellationToken)
        {
            var version = AccountService.NewId();
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            await db.Billing.Where(row => row.OrganizationId == organizationId && row.OverageStartsAt == month)
                .ExecuteUpdateAsync(row => row.SetProperty(binding => binding.OverageStartsAt, next).SetProperty(binding => binding.Version, version), cancellationToken);
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
