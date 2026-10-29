
namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed record NotificationInboxSnapshot(IReadOnlyList<NotificationRecord> Items, string Status, int Unread,
        int Critical, int Total, int Matching, int Page, int PageCount);
    public sealed record NotificationToolItem(string Id, string Type, string Title, string Body, string Status, string Severity, DateTimeOffset CreatedAt);
    public sealed record NotificationToolList(IReadOnlyList<NotificationToolItem> Notifications, string Status, int Unread,
        int Critical, int Total, int Matching, int Page, int PageCount);
    public sealed record NotificationsMarked(int Marked);

    // Shared pagination and CAS mutation keep native forms and MCP consistent.
    public static class NotificationCommands
    {
        public static Task<AccountContext> RefreshAsync(AccountService accounts, AccountContext context, CancellationToken cancellationToken, bool reload = false)
        {
            return AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: context.Role.Length == 0, reload: reload);
        }

        public static NotificationInboxSnapshot Inbox(AccountContext context, string? status, int requestedPage)
        {
            var visible = context.Account.Notifications.Where(item => item.VisibleTo(context)).ToArray();
            var filter = string.Equals(status, "unread", StringComparison.OrdinalIgnoreCase) ? "unread" : "all";
            var matching = visible.Where(item => filter != "unread" || item.Unread).OrderByDescending(item => item.CreatedAt).ToArray();
            var count = Math.Max(1, (matching.Length + 9) / 10);
            var page = Math.Clamp(requestedPage, 1, count);
            return new([.. matching.Skip((page - 1) * 10).Take(10)], filter,
                visible.Count(item => item.Unread),
                visible.Count(item => item.Unread && (item.Severity.Equals("danger", StringComparison.OrdinalIgnoreCase) || item.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase))),
                visible.Length, matching.Length, page, count);
        }

        public static async Task<int> MarkAsync(AccountService accounts, AccountContext context, string? notificationId, CancellationToken cancellationToken)
        {
            context = await RefreshAsync(accounts, context, cancellationToken);
            var marked = 0;
            await accounts.UpdateAccountAsync(context.Session, account =>
            {
                var visible = account.Notifications.Where(item => item.VisibleTo(context) && (notificationId is null || item.Id == notificationId)).ToArray();
                if (notificationId is not null && visible.Length == 0)
                {
                    throw new AccountAccessException("Notification is unavailable.", 404);
                }
                // The callback can run again after a stale producer version. Count
                // only the changes in the attempt that actually commits.
                marked = visible.Count(item => item.Unread);
                foreach (var item in visible)
                {
                    item.Status = "Read";
                }
            }, cancellationToken);
            return marked;
        }
    }
}
