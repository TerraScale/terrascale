using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Common.Formatting;
using TerraScale.Dashboard.Features.Billing;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.TerraBase.Contracts;
using TerraBaseSqlErrorCodes = TerraScale.DotNet.TerraBase.TerraBaseSqlErrorCodes;
using TerraBaseSqlException = TerraScale.DotNet.TerraBase.TerraBaseSqlException;

namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed record AllowanceUsageObservation(string OrganizationId, long? Operations, long? LogicalBytes, long? SqlReads = null, long? StorageByteHours = null);
    public sealed record AllowanceUsagePage(DateTimeOffset PeriodStart, DateTimeOffset ObservedAt, IReadOnlyList<AllowanceUsageObservation> Items, string? Next, string OperationsContract, string StorageContract);
    public sealed record AllowanceAlert(string Id, string OrganizationId, string BillingMonth, string Metric, int Threshold, long Used, long Allowance,
        DateTimeOffset CreatedAt, string EmailStatus = "pending")
    {
        public NotificationRecord Notification()
        {
            return new()
            {
                Id = Id,
                OrganizationId = OrganizationId,
                BillingMonth = BillingMonth,
                Type = Metric == "spend" ? "spend-cap" : "allowance-" + Metric,
                Title = Metric == "spend" ? "Monthly spending alert reached" : $"{(Metric == "operations" ? "Operations" : "Storage")} allowance: {Threshold}% reached",
                Body = Metric == "spend"
                    ? string.Create(CultureInfo.InvariantCulture, $"Month-to-date paid usage {MoneyFormat.Money(Used)} reached the {MoneyFormat.Money(Allowance)} spending alert for {BillingMonth}. Report only; services remain active.")
                    : string.Create(CultureInfo.InvariantCulture, $"{Used:N0} of {Allowance:N0} {(Metric == "operations" ? "operation units" : "bytes")} used for {BillingMonth}. Review usage and your plan."),
                Severity = Threshold == 100 ? "danger" : "warning",
                Status = "Unread",
                Url = "/usage",
                CreatedAt = CreatedAt
            };
        }
    }
    public interface IAllowanceNotificationBackend
    {
        Task<AllowanceUsagePage> ReadAsync(string? after, bool previous, CancellationToken cancellationToken);
        Task<string> SendAsync(AllowanceAlert alert, CancellationToken cancellationToken);
    }

    public sealed class AllowanceNotifications(IDbContextFactory<DashboardDbContext> storage, OrganizationStore organizations, IAllowanceNotificationBackend backend, TimeProvider clock, ILogger<AllowanceNotifications> logger)
    {
        public const string OperationsContract = "write-units-4kib-global-v2";
        public const string StorageContract = "stored-byte-hours-global-v1";

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            string? after = null;
            do
            {
                var page = await backend.ReadAsync(after, false, cancellationToken);
                var month = ValidatedMonth(page, after, clock.GetUtcNow());
                foreach (var item in page.Items)
                {
                    try { await ObserveAsync(item, month, cancellationToken); }
                    catch (Exception error) when (error is TerraBaseException or DbUpdateException or HttpRequestException or IOException or JsonException or InvalidOperationException ||
                        error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                    {
                        logger.AnAllowanceNotificationForOrganizationCould(item.OrganizationId, error.GetType().Name);
                    }
                }
                after = page.Next;
            } while (after is not null);
        }

        public static DateTimeOffset ValidatedMonth(AllowanceUsagePage page, string? after, DateTimeOffset now, bool previous = false)
        {
            if (page.Items.Count > 0 && page.OperationsContract != OperationsContract)
            {
                throw new IOException("Allowance metering does not report authoritative global 4 KiB write units.");
            }

            var current = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var month = previous ? current.AddMonths(-1) : current;
            if (page.PeriodStart != month || page.ObservedAt < current || page.ObservedAt > now || now - page.ObservedAt > TimeSpan.FromMinutes(5) ||
                page.Items.Count > 25 || page.Items.Any(item => !TerraScaleIds.IsValid(item.OrganizationId) || item.Operations < 0 || item.LogicalBytes < 0 || item.SqlReads < 0 || item.StorageByteHours < 0) ||
                page.Items.Select(item => item.OrganizationId).Distinct(StringComparer.Ordinal).Count() != page.Items.Count ||
                page.Items.Any(item => after is not null && string.CompareOrdinal(item.OrganizationId, after) <= 0) ||
                page.Next is not null && (!TerraScaleIds.IsValid(page.Next) || page.Items.Count == 0 || page.Items[^1].OrganizationId != page.Next || string.CompareOrdinal(page.Next, after) <= 0))
            {
                throw new IOException("Allowance producer returned an invalid billing period or page.");
            }

            return month;
        }

        private async Task ObserveAsync(AllowanceUsageObservation item, DateTimeOffset month, CancellationToken cancellationToken)
        {
            var stored = await organizations.FindAsync(item.OrganizationId, cancellationToken);
            if (stored is not { Erased: false })
            {
                return;
            }

            var organization = stored.Organization;

            if (organization.Status != "active" || organization.Archived || organization.DeletionRequested || PlanCatalog.For(organization) is null)
            {
                return;
            }

            var limits = PlanCatalog.Limits(organization);
            var billingMonth = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                var alerts = await db.AllowanceAlerts.Where(alert => alert.OrganizationId == item.OrganizationId).ToListAsync(cancellationToken);
                db.AllowanceAlerts.RemoveRange(alerts.Where(alert => alert.BillingMonth != billingMonth));
                var current = alerts.Where(alert => alert.BillingMonth == billingMonth).ToList();
                if (item.Operations is { } used && limits.MonthlyOperations is > 0 and < long.MaxValue)
                {
                    foreach (var threshold in new[] { 80, 100 })
                    {
                        if ((decimal)used * 100 < (decimal)limits.MonthlyOperations * threshold || current.Any(alert => alert.Metric == "operations" && alert.Threshold == threshold))
                        {
                            continue;
                        }

                        db.AllowanceAlerts.Add(Row(item.OrganizationId, billingMonth, "operations", threshold, used, limits.MonthlyOperations));
                    }
                }

                if (item.Operations is { } spent && organization.InternalBilling is null && PlanCatalog.For(organization) is { PriceCents: > 0 } plan &&
                    organization.SpendCapCents > 0 && !current.Any(alert => alert.Metric == "spend" && alert.Allowance == organization.SpendCapCents))
                {
                    var monthToDateCents = OperationUnitOverage.Cents(OperationUnitOverage.Units(plan, spent));
                    if (monthToDateCents >= organization.SpendCapCents)
                    {
                        db.AllowanceAlerts.RemoveRange(current.Where(alert => alert.Metric == "spend"));
                        db.AllowanceAlerts.Add(Row(item.OrganizationId, billingMonth, "spend", 100, monthToDateCents, organization.SpendCapCents));
                    }
                }

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    break;
                }
                catch (DbUpdateException error) when (attempt < 2 && (error is DbUpdateConcurrencyException || error.InnerException is TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.UniqueViolation }))
                {
                    db.ChangeTracker.Clear();
                }
            }

            if (item.Operations is null)
            {
                return;
            }

            var pending = await db.AllowanceAlerts.AsNoTracking()
                .Where(alert => alert.OrganizationId == item.OrganizationId && alert.BillingMonth == billingMonth && alert.Metric == "operations" && alert.EmailStatus == "pending")
                .OrderBy(alert => alert.Id).ToListAsync(cancellationToken);
            foreach (var alert in pending)
            {
                var outcome = await backend.SendAsync(Alert(alert), cancellationToken);
                if (outcome == "retryable")
                {
                    continue;
                }

                await db.AllowanceAlerts.Where(row => row.Id == alert.Id && row.EmailStatus == "pending")
                    .ExecuteUpdateAsync(row => row.SetProperty(saved => saved.EmailStatus, outcome), cancellationToken);
            }
        }

        private AllowanceAlertRow Row(string organizationId, string billingMonth, string metric, int threshold, long used, long allowance)
        {
            return new()
            {
                Id = AccountService.NewId(),
                OrganizationId = organizationId,
                BillingMonth = billingMonth,
                Metric = metric,
                Threshold = threshold,
                Used = used,
                Allowance = allowance,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
                EmailStatus = "pending",
            };
        }

        public static AllowanceAlert Alert(AllowanceAlertRow row)
        {
            return new(row.Id, row.OrganizationId, row.BillingMonth, row.Metric, row.Threshold, row.Used, row.Allowance,
                new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)), row.EmailStatus);
        }
    }

    public static class AllowanceNotificationInbox
    {
        public static async Task HydrateAsync(IDbContextFactory<DashboardDbContext> storage, AccountStore accounts, AccountContext context, CancellationToken cancellationToken)
        {
            if (context.Organization is not { } organization || organization.RoleFor(context.UserId) != "owner")
            {
                return;
            }

            List<AllowanceAlertRow> alerts;
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                alerts = await db.AllowanceAlerts.AsNoTracking().Where(alert => alert.OrganizationId == organization.Id).OrderBy(alert => alert.Id).ToListAsync(cancellationToken);
            }

            if (alerts.Count == 0)
            {
                return;
            }

            for (var attempt = 0; attempt < 8; attempt++)
            {
                var stored = await accounts.FindAsync(context.UserId, cancellationToken);
                if (stored is not { Erased: false } || stored.Account.Id != context.Account.Id)
                {
                    throw new IOException("Allowance owner inbox is unavailable.");
                }

                var account = stored.Account;

                var missing = alerts.Where(alert => !account.Notifications.Any(notification => notification.Id == alert.Id)).ToArray();
                if (missing.Length == 0) { context.Account.Notifications = account.Notifications; return; }
                account.Notifications.AddRange(missing.Select(alert => AllowanceNotifications.Alert(alert).Notification()));
                try
                {
                    await accounts.ReplaceAsync(stored, cancellationToken);
                    context.Account.Notifications = account.Notifications;
                    return;
                }
                catch (StateConflictException) when (attempt < 7) { }
            }
        }
    }
}
