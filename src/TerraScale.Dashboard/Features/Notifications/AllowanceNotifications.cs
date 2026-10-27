using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Notifications;

public sealed record AllowanceUsageObservation(string OrganizationId, long? Operations, long? LogicalBytes);
public sealed record AllowanceUsagePage(DateTimeOffset PeriodStart, DateTimeOffset ObservedAt, IReadOnlyList<AllowanceUsageObservation> Items, string? Next, string OperationsContract, string StorageContract);
public sealed record AllowanceAlert(string Id, string OrganizationId, string BillingMonth, string Metric, int Threshold, long Used, long Allowance,
    DateTimeOffset CreatedAt, string EmailStatus = "pending")
{
    public NotificationRecord Notification() => new()
    {
        Id = Id,
        OrganizationId = OrganizationId,
        BillingMonth = BillingMonth,
        Type = "allowance-" + Metric,
        Title = $"{(Metric == "operations" ? "Operations" : "Storage")} allowance: {Threshold}% reached",
        Body = string.Create(CultureInfo.InvariantCulture, $"{Used:N0} of {Allowance:N0} {(Metric == "operations" ? "operation units" : "bytes")} used for {BillingMonth}. Review usage and your plan."),
        Severity = Threshold == 100 ? "danger" : "warning",
        Status = "Unread",
        Url = "/usage",
        CreatedAt = CreatedAt
    };
}
public sealed record AllowanceNotificationState(string OrganizationId, List<AllowanceAlert> Alerts);

public interface IAllowanceNotificationBackend
{
    Task<AllowanceUsagePage> ReadAsync(string? after, CancellationToken cancellationToken);
    Task<string> SendAsync(AllowanceAlert alert, CancellationToken cancellationToken);
}

public sealed class AllowanceNotifications(IDashboardState state, IAllowanceNotificationBackend backend, TimeProvider clock, ILogger<AllowanceNotifications> logger)
{
    public const string Collection = "dashboard_allowance_alerts";
    public const string OperationsContract = "operation-units-4kib-global-v1";

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        string? after = null;
        do
        {
            var page = await backend.ReadAsync(after, cancellationToken);
            var month = ValidatedMonth(page, after, clock.GetUtcNow());
            foreach (var item in page.Items)
            {
                try { await ObserveAsync(item, month, cancellationToken); }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    // Messages here are the dashboard's own or status lines; no response body or recipient is logged.
                    logger.LogWarning("An allowance notification for organization {OrganizationId} could not be processed ({ErrorType}: {Error}); other organizations continue.",
                        item.OrganizationId, error.GetType().Name, error.Message);
                }
            }
            after = page.Next;
        } while (after is not null);
    }

    // The usage feed is trusted only for the current UTC month, fresh, and in strictly ascending organization order.
    public static DateTimeOffset ValidatedMonth(AllowanceUsagePage page, string? after, DateTimeOffset now)
    {
        if (page.Items.Count > 0 && page.OperationsContract != OperationsContract)
            throw new IOException("Allowance metering does not report authoritative global 4 KiB operation units.");
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        if (page.PeriodStart != month || page.ObservedAt < month || page.ObservedAt > now || now - page.ObservedAt > TimeSpan.FromMinutes(5) ||
            page.Items.Count > 25 || page.Items.Any(item => !TerraScaleIds.IsValid(item.OrganizationId) || item.Operations < 0 || item.LogicalBytes < 0) ||
            page.Items.Select(item => item.OrganizationId).Distinct(StringComparer.Ordinal).Count() != page.Items.Count ||
            page.Items.Any(item => after is not null && string.CompareOrdinal(item.OrganizationId, after) <= 0) ||
            page.Next is not null && (!TerraScaleIds.IsValid(page.Next) || page.Items.Count == 0 || page.Items[^1].OrganizationId != page.Next || string.CompareOrdinal(page.Next, after) <= 0))
            throw new IOException("Allowance producer returned an invalid billing period or page.");
        return month;
    }

    private async Task ObserveAsync(AllowanceUsageObservation item, DateTimeOffset month, CancellationToken cancellationToken)
    {
        var organizationDocument = await state.GetAsync(AccountService.OrganizationsCollection, item.OrganizationId, cancellationToken);
        if (organizationDocument is null) return;
        var organization = AccountService.Read<OrganizationRecord>(organizationDocument);
        if (organizationDocument.Id != item.OrganizationId || organization.Id != item.OrganizationId) throw new IOException("Allowance organization identity is invalid.");
        if (organization.Status != "active" || organization.Archived || organization.DeletionRequested || PlanCatalog.For(organization) is null) return;
        var limits = PlanCatalog.Limits(organization);
        var billingMonth = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        AllowanceNotificationState? saved = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var document = await state.GetAsync(Collection, item.OrganizationId, cancellationToken);
            var current = document is null ? new AllowanceNotificationState(item.OrganizationId, []) : Read(document, item.OrganizationId);
            current.Alerts.RemoveAll(alert => string.CompareOrdinal(alert.BillingMonth, billingMonth) < 0);
            if (item.Operations is { } used && limits.MonthlyOperations is > 0 and < long.MaxValue)
            {
                foreach (var threshold in new[] { 80, 100 })
                {
                    if ((decimal)used * 100 < (decimal)limits.MonthlyOperations * threshold || current.Alerts.Any(alert => alert.BillingMonth == billingMonth && alert.Metric == "operations" && alert.Threshold == threshold)) continue;
                    current.Alerts.Add(new(AccountService.NewId(), item.OrganizationId, billingMonth, "operations", threshold, used, limits.MonthlyOperations, clock.GetUtcNow()));
                }
            }
            try
            {
                await state.PutAsync(Collection, item.OrganizationId, JsonSerializer.SerializeToElement(current),
                    document is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(document.Version), cancellationToken);
                saved = current;
                break;
            }
            catch (TerraBasePreconditionFailedException) when (attempt < 7) { }
        }
        if (saved is null) throw new IOException("Allowance alert reservation failed.");
        foreach (var alert in saved.Alerts.Where(alert => alert.BillingMonth == billingMonth && alert.EmailStatus == "pending" &&
            alert.Metric == "operations" && item.Operations is not null))
        {
            // ts-auth owns the send claim, so a crash after its response can safely repeat this call.
            var outcome = await backend.SendAsync(alert, cancellationToken);
            if (outcome == "retryable") continue;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var document = await state.GetAsync(Collection, item.OrganizationId, cancellationToken) ?? throw new IOException("Allowance alert disappeared.");
                var current = Read(document, item.OrganizationId);
                var index = current.Alerts.FindIndex(item => item.Id == alert.Id);
                if (index < 0 || current.Alerts[index].EmailStatus != "pending") break;
                current.Alerts[index] = current.Alerts[index] with { EmailStatus = outcome };
                try { await state.PutAsync(Collection, item.OrganizationId, JsonSerializer.SerializeToElement(current), TerraBasePrecondition.Matches(document.Version), cancellationToken); break; }
                catch (TerraBasePreconditionFailedException) when (attempt < 7) { }
            }
        }
    }

    public static AllowanceNotificationState Read(TerraBaseDocument document, string organizationId)
    {
        var saved = AccountService.Read<AllowanceNotificationState>(document);
        if (document.Id != organizationId || saved.OrganizationId != organizationId || saved.Alerts is null || saved.Alerts.Count > 4 ||
            saved.Alerts.Any(alert => !TerraScaleIds.IsValid(alert.Id) || alert.OrganizationId != organizationId || alert.Metric is not ("operations" or "storage") || alert.Threshold is not (80 or 100)))
            throw new IOException("Allowance alert identity is invalid.");
        return saved;
    }
}

public static class AllowanceNotificationInbox
{
    // Called only after ts-auth hydrated the selected organization's live owner role.
    public static async Task HydrateAsync(IDashboardState state, AccountContext context, CancellationToken cancellationToken)
    {
        if (context.Organization is not { } organization || organization.RoleFor(context.UserId) != "owner") return;
        var ledger = await state.GetAsync(AllowanceNotifications.Collection, organization.Id, cancellationToken);
        if (ledger is null) return;
        var alerts = AllowanceNotifications.Read(ledger, organization.Id).Alerts;
        var key = AccountService.AccountStorageKey(context.UserId);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var document = await state.GetAsync(AccountService.AccountsCollection, key, cancellationToken) ?? throw new IOException("Allowance owner inbox is unavailable.");
            var account = AccountService.Read<AccountRecord>(document);
            var identityDocument = await state.GetAsync(AccountService.IdentitiesCollection, key, cancellationToken) ?? throw new IOException("Allowance owner identity is unavailable.");
            var identity = AccountService.Read<AccountIdentityBinding>(identityDocument);
            if (document.Id != key || identityDocument.Id != key || account.Id != context.Account.Id || account.UserId != context.UserId || identity.UserId != context.UserId || identity.AccountId != account.Id)
                throw new IOException("Allowance owner identity is invalid.");
            var missing = alerts.Where(alert => !account.Notifications.Any(notification => notification.Id == alert.Id)).ToArray();
            if (missing.Length == 0) { context.Account.Notifications = account.Notifications; return; }
            account.Notifications.AddRange(missing.Select(alert => alert.Notification()));
            try
            {
                await state.PutAsync(AccountService.AccountsCollection, key, JsonSerializer.SerializeToElement(account), TerraBasePrecondition.Matches(document.Version), cancellationToken);
                context.Account.Notifications = account.Notifications;
                return;
            }
            catch (TerraBasePreconditionFailedException) when (attempt < 7) { }
        }
    }
}
