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
        public async Task<long> OperationUnitOverageAsync(BillingBinding binding, DateTimeOffset now, CancellationToken cancellationToken)
        {
            string? after = null;
            do
            {
                var page = await usage.ReadAsync(after, null, cancellationToken);
                AllowanceNotifications.ValidatedMonth(page, after, now, null);
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
            var pending = await db.OverageCharges.AsNoTracking().Where(row => row.ProviderId == null && row.AmountCents > 0).ToListAsync(cancellationToken);
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
            var starts = await db.Billing.AsNoTracking().Where(row => row.Kind == "stripe" && row.SubscriptionId != "" && row.State != "deleting" && row.ErasureId == null).Select(row => row.OverageStartsAt).ToListAsync(cancellationToken);
            var now = clock.GetUtcNow();
            var current = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            for (var month = starts.Count > 0 ? starts.Min() : current; month < current; month = month.AddMonths(1))
            {
                try { await RunMonthAsync(month, cancellationToken); }
                catch (Exception error) when (IsReportingFailure(error, cancellationToken))
                {
                    logger.UsageOverageReportingIsUnavailableRetrying(error.GetType().Name);
                }
            }
        }

        private static bool IsReportingFailure(Exception error, CancellationToken cancellationToken) =>
            error is TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or StripeException ||
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
            if (binding is null || binding.State == "deleting" || binding.ErasureId is not null || binding.Kind != "stripe" || binding.CustomerId.Length == 0 || binding.SubscriptionId.Length == 0 || month < binding.OverageStartsAt) { return; }
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            if (await db.SkippedBillingMonths.AnyAsync(row => row.OrganizationId == organizationId && row.SubscriptionId == binding.SubscriptionId && row.Month == month, cancellationToken)) { return; }
            var terms = await db.BillingMonths.AsNoTracking().SingleOrDefaultAsync(row => row.OrganizationId == organizationId && row.SubscriptionId == binding.SubscriptionId && row.Month == month, cancellationToken);
            if (terms is null || storageByteHours is null)
            {
                var reason = terms is null ? "missing_plan" : "missing_storage_contract";
                if (await storage.InsertAsync(new SkippedBillingMonth { OrganizationId = organizationId, SubscriptionId = binding.SubscriptionId, Month = month, Reason = reason }, cancellationToken))
                {
                    billing.RecordSkippedOverageMonth(organizationId, month, reason);
                }
                return;
            }
            ArgumentOutOfRangeException.ThrowIfNegative(storageByteHours.Value);
            var hours = (long)(month.AddMonths(1) - month).TotalHours;
            var storageAllowance = checked(terms.StorageAllowance * hours);
            var storageQuantity = (long)BigInteger.Max(0, ((BigInteger)storageByteHours.Value - storageAllowance) / hours / 1_000_000);
            var operations = await FreezeAsync(binding, terms, OperationUnitOverage.Kind, units, terms.OperationsAllowance, terms.OperationRate, Math.Max(0, units - terms.OperationsAllowance), cancellationToken);
            var stored = await FreezeAsync(binding, terms, StorageOverage.Kind, storageByteHours.Value, storageAllowance, terms.StorageRate, storageQuantity, cancellationToken);
            foreach (var charge in new[] { operations, stored })
            {
                if (charge.AmountCents > 0 && charge.ProviderId is null)
                {
                    try { await billing.ReportOverageAsync(charge, cancellationToken); }
                    catch (Exception error) when (IsReportingFailure(error, cancellationToken))
                    {
                        logger.UsageOverageCouldNotBeReported(error.GetType().Name);
                    }
                }
            }
        }

        private async Task<OverageCharge> FreezeAsync(BillingBinding binding, BillingMonth terms, string meter, long used, long allowance, decimal rate, long quantity, CancellationToken cancellationToken)
        {
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
                AmountCents = checked((long)Math.Round(quantity * rate, 0, MidpointRounding.AwayFromZero)),
                Description = string.Create(CultureInfo.InvariantCulture, $"{meter}, {terms.PlanName}, {terms.Month:yyyy-MM}: {quantity}")
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
                catch (Exception error) when (error is TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or StripeException or OperationCanceledException)
                {
                    logger.UsageOverageReportingIsUnavailableRetrying(error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }
}
