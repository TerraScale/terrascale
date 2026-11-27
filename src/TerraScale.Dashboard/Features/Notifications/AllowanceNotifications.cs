using Dapper;
using System.Data;
using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Common.Formatting;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Clients;
using TerraBaseSqlErrorCodes = TerraScale.DotNet.TerraBase.TerraBaseSqlErrorCodes;
using TerraBaseSqlException = TerraScale.DotNet.TerraBase.TerraBaseSqlException;

namespace TerraScale.Dashboard.Features.Notifications
{
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
        Task<string> SendAsync(AllowanceAlert alert, CancellationToken cancellationToken);
    }

    public sealed class AllowanceNotifications(DashboardDb storage, OrganizationStore organizations, IAllowanceNotificationBackend backend, OrganizationUsage usage, TimeProvider clock, ILogger<AllowanceNotifications> logger, BackgroundJobs jobs, BillingSchemaGate gate)
    {
        private const string AlertsOf = DashboardSql.AllowanceAlerts.Select + " WHERE organization_id = @OrganizationId";
        private const string RemoveJob = "DELETE FROM background_jobs WHERE id = @Id";
        private const string PendingEmails = DashboardSql.AllowanceAlerts.Select
            + " WHERE organization_id = @OrganizationId AND billing_month = @BillingMonth AND metric = 'operations' AND email_status = 'pending' ORDER BY id";
        private const string PendingEmail = DashboardSql.AllowanceAlerts.Select + " WHERE id = @Id AND email_status = 'pending'";
        private const string SetEmailStatus = "UPDATE allowance_alerts SET email_status = @EmailStatus WHERE id = @Id";

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            if (!await gate.AllowsAsync(cancellationToken)) { return; }
            var month = OrganizationUsage.MonthOf(clock.GetUtcNow());
            var candidates = (await organizations.ListAsync(cancellationToken)).Where(Observed).Select(static organization => organization.Id).ToList();
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = OrganizationUsage.Concurrency, CancellationToken = cancellationToken },
                async (organizationId, token) =>
                {
                    var read = await usage.ReadAsync(organizationId, month, token);
                    var operations = read switch
                    {
                        MeasuredUsage measured => measured.OperationUnits,
                        PartialUsage partial => partial.OperationUnits,
                        UsageUnavailable unavailable => Unavailable(unavailable),
                    };
                    if (operations is not { } used)
                    {
                        return;
                    }

                    try { await ObserveAsync(organizationId, used, month, token); }
                    catch (Exception error) when (error is TerraBaseException or DBConcurrencyException or HttpRequestException or IOException or JsonException or InvalidOperationException ||
                        error is OperationCanceledException && !token.IsCancellationRequested)
                    {
                        logger.AnAllowanceNotificationForOrganizationCould(organizationId, error.GetType().Name);
                    }
                });
        }

        private long? Unavailable(UsageUnavailable unavailable)
        {
            BillingLog.UsageUnavailable(logger, unavailable.OrganizationId, unavailable.Reason);
            return null;
        }

        private static bool Observed(OrganizationRecord organization) =>
            organization.Status == "active" && !organization.Archived && !organization.DeletionRequested && PlanCatalog.For(organization) is not null;

        private async Task ObserveAsync(string organizationId, long used, DateTimeOffset month, CancellationToken cancellationToken)
        {
            var stored = await organizations.FindAsync(organizationId, cancellationToken);
            if (stored is not { Erased: false })
            {
                return;
            }

            var organization = stored.Organization;

            if (!Observed(organization))
            {
                return;
            }

            var limits = PlanCatalog.Limits(organization);
            var billingMonth = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            for (var attempt = 0; ; attempt++)
            {
                List<AllowanceAlertRow> alerts;
                await using (var connection = await storage.OpenAsync(cancellationToken))
                {
                    alerts = (await connection.QueryAsync<AllowanceAlertRow>(AlertsOf, new { OrganizationId = organizationId })).AsList();
                }

                List<AllowanceAlertRow> removed = [.. alerts.Where(alert => alert.BillingMonth != billingMonth)];
                List<AllowanceAlertRow> added = [];
                var current = alerts.Where(alert => alert.BillingMonth == billingMonth).ToList();
                if (limits.MonthlyOperations is > 0 and < long.MaxValue)
                {
                    foreach (var threshold in new[] { 80, 100 })
                    {
                        if ((decimal)used * 100 < (decimal)limits.MonthlyOperations * threshold || current.Any(alert => alert.Metric == "operations" && alert.Threshold == threshold))
                        {
                            continue;
                        }

                        added.Add(Row(organizationId, billingMonth, "operations", threshold, used, limits.MonthlyOperations));
                    }
                }

                if (organization.InternalBilling is null && PlanCatalog.For(organization) is { PriceCents: > 0 } plan &&
                    organization.SpendCapCents > 0 && !current.Any(alert => alert.Metric == "spend" && alert.Allowance == organization.SpendCapCents))
                {
                    var monthToDateCents = OperationUnitOverage.Cents(OperationUnitOverage.Units(plan, used));
                    if (monthToDateCents >= organization.SpendCapCents)
                    {
                        removed.AddRange(current.Where(alert => alert.Metric == "spend"));
                        added.Add(Row(organizationId, billingMonth, "spend", 100, monthToDateCents, organization.SpendCapCents));
                    }
                }

                if (removed.Count == 0 && added.Count == 0)
                {
                    break;
                }

                try
                {
                    await storage.WriteAsync(async (connection, transaction) =>
                    {
                        foreach (var alert in removed)
                        {
                            await connection.ExecuteAsync(DashboardSql.AllowanceAlerts.Delete, alert, transaction);
                            await connection.ExecuteAsync(RemoveJob, new { Id = "allowance/" + alert.Id }, transaction);
                        }

                        foreach (var alert in added)
                        {
                            await connection.ExecuteAsync(DashboardSql.AllowanceAlerts.Insert, alert, transaction);
                        }
                    }, cancellationToken);
                    break;
                }
                catch (Exception error) when (attempt < 2 && (DashboardDb.IsConflict(error) || DashboardDb.IsDuplicate(error)))
                {
                    await DashboardDb.ConflictBackoffAsync(attempt, clock, cancellationToken);
                }
            }

            List<AllowanceAlertRow> pending;
            await using (var connection = await storage.OpenAsync(cancellationToken))
            {
                pending = (await connection.QueryAsync<AllowanceAlertRow>(PendingEmails, new { OrganizationId = organizationId, BillingMonth = billingMonth })).AsList();
            }

            foreach (var alert in pending)
            {
                await jobs.RunAsync("allowance/" + alert.Id, TimeSpan.Zero, async (claim, token) =>
                {
                    var current = await claim.Connection.QuerySingleOrDefaultAsync<AllowanceAlertRow>(PendingEmail, new { alert.Id });
                    if (current is null)
                    {
                        await claim.ForgetAsync(token);
                        return;
                    }

                    var outcome = await backend.SendAsync(Alert(current), token);
                    if (outcome != "retryable")
                    {
                        await claim.SaveAsync(
                            (connection, transaction) => connection.ExecuteAsync(SetEmailStatus, new { current.Id, EmailStatus = outcome }, transaction),
                            token);
                    }
                }, cancellationToken);
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
        private const string Ordered = DashboardSql.AllowanceAlerts.Select + " WHERE organization_id = @OrganizationId ORDER BY id";

        public static async Task HydrateAsync(DashboardDb storage, AccountStore accounts, TimeProvider clock, AccountContext context, CancellationToken cancellationToken)
        {
            if (context.Organization is not { } organization || organization.RoleFor(context.UserId) != "owner")
            {
                return;
            }

            List<AllowanceAlertRow> alerts;
            await using (var connection = await storage.OpenAsync(cancellationToken))
            {
                alerts = (await connection.QueryAsync<AllowanceAlertRow>(Ordered, new { OrganizationId = organization.Id })).AsList();
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
                catch (DBConcurrencyException) when (attempt < 7) { await DashboardDb.ConflictBackoffAsync(attempt, clock, cancellationToken); }
            }
        }
    }
}
