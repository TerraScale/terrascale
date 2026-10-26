using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Shared.Formatting;

namespace TerraScale.Dashboard.Features.ApiKeys;

// A key that expires within seven days puts one notification in each viewer's inbox. ts-auth's key list is the
// source; the account's persisted notifications hold the item, one per key (its expiry cannot change).
public static class ApiKeyExpiryNotifications
{
    public const string Type = "api-key-expiring";

    public static NotificationRecord[] Due(AccountContext context, IEnumerable<ProjectCredential> keys, IEnumerable<NotificationRecord> inbox, DateTimeOffset now)
    {
        var known = inbox.Where(item => item.Type == Type).Select(item => item.Url).ToHashSet(StringComparer.Ordinal);
        return keys.Where(key => ApiKeyService.ExpiresSoon(key, now) && !known.Contains(Url(key))).Select(key => new NotificationRecord
        {
            Id = AccountService.NewId(),
            OrganizationId = context.Organization?.Id ?? "",
            ProjectId = key.ProjectId,
            Type = Type,
            Title = $"API key {key.Name} expires on {key.ExpiresAt!.Value.UtcDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}",
            Body = $"Requests with this key fail after {ValueFormat.UtcDate(key.ExpiresAt.Value)}. Create a replacement key and move your applications to it.",
            Severity = "warning",
            Status = "Unread",
            Url = Url(key),
            CreatedAt = now
        }).ToArray();
    }

    public static async Task AddAsync(AccountService accounts, AccountContext context, IReadOnlyList<ProjectCredential> keys, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (Due(context, keys, context.Account.Notifications, now).Length == 0) return;
        NotificationRecord[] added = [];
        // The notice never blocks the page that found it; the next view retries.
        try { await accounts.UpdateAccountAsync(context.Session, account => account.Notifications.AddRange(added = Due(context, keys, account.Notifications, now)), cancellationToken); }
        catch (Exception error) when (error is HttpRequestException or AccountAccessException or IOException or JsonException) { return; }
        context.Account.Notifications.AddRange(added);
    }

    // The inbox lists live keys too: a ts-auth outage leaves it as it was.
    public static async Task RefreshAsync(HttpContext http, AccountContext context)
    {
        if (context.Project is null) return;
        var services = http.RequestServices;
        try
        {
            var keys = await services.GetRequiredService<ApiKeyService>().ListAsync(context, http.RequestAborted);
            await AddAsync(services.GetRequiredService<AccountService>(), context, keys, services.GetRequiredService<TimeProvider>().GetUtcNow(), http.RequestAborted);
        }
        catch (Exception error) when (error is HttpRequestException or AccountAccessException) { }
    }

    private static string Url(ProjectCredential key) => "/api-keys?q=" + key.Id;
}
