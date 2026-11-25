using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Common.Formatting;
using TerraScale.Dashboard.Platform.Http;

namespace TerraScale.Dashboard.Features.ApiKeys
{
    public sealed class ApiKeyExpiryNotifications(ApiKeyService keys, AccountService accounts, TimeProvider time, ILogger<ApiKeyExpiryNotifications> logger)
    {
        public const string Type = "api-key-expiring";
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
        private const int MaxTracked = 10_000;

        private readonly ConcurrentDictionary<string, DateTimeOffset> checkedAt = new(StringComparer.Ordinal);

        public static NotificationRecord[] Due(AccountContext context, IEnumerable<ProjectCredential> keys, IEnumerable<NotificationRecord> inbox, DateTimeOffset now)
        {
            var known = inbox.Where(item => item.Type == Type).Select(item => item.Url).ToHashSet(StringComparer.Ordinal);
            return [.. keys.Where(key => ApiKeyService.ExpiresSoon(key, now) && !known.Contains(Url(key))).Select(key => new NotificationRecord
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
            })];
        }

        public static async Task AddAsync(AccountService accounts, AccountContext context, IReadOnlyList<ProjectCredential> keys, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (Due(context, keys, context.Account.Notifications, now).Length == 0)
            {
                return;
            }

            NotificationRecord[] added = [];
            await accounts.UpdateAccountAsync(context.Session, account => account.Notifications.AddRange(added = Due(context, keys, account.Notifications, now)), cancellationToken);
            context.Account.Notifications.AddRange(added);
        }

        public async Task RefreshAsync(AccountContext context, CancellationToken cancellationToken)
        {
            if (context.Project is not { } project || !context.CanManageKeys)
            {
                return;
            }

            var now = time.GetUtcNow();
            var scope = context.UserId + ":" + project.Id;
            if (checkedAt.TryGetValue(scope, out var last) && now - last < Interval)
            {
                return;
            }

            if (checkedAt.Count >= MaxTracked)
            {
                foreach (var (stale, at) in checkedAt)
                {
                    if (now - at >= Interval)
                    {
                        checkedAt.TryRemove(stale, out _);
                    }
                }
            }

            checkedAt[scope] = now;
            try
            {
                await AddAsync(accounts, context, await keys.ListAsync(context, cancellationToken), now, cancellationToken);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException or AccountAccessException or IOException or JsonException)
            {
                checkedAt.TryRemove(scope, out _);
                logger.ApiKeyExpiryRefreshFailed(project.Id, error.GetType().Name);
            }
        }

        private static string Url(ProjectCredential key)
        {
            return "/api-keys/" + key.Id;
        }
    }
}
