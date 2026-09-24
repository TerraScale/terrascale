using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Notifications.Pages.Notifications;

public sealed record NotificationsPageModel(
    int Unread,
    int Critical,
    int Total,
    IReadOnlyList<Notification> Notifications,
    string Status,
    int FilteredCount,
    int Page,
    int PageCount) : IPageModel
{
    public const int PageSize = 10;

    public ShellModel Shell { get; init; } = null!;

    public static NotificationsPageModel From(DashboardStore store, string href, string? status = null, int page = 1)
    {
        var currentStatus = status?.Trim().ToLowerInvariant() == "unread" ? "unread" : "all";
        var filtered = currentStatus == "unread"
            ? store.Notifications.Where(n => n.Status == "Unread").ToArray()
            : store.Notifications.ToArray();
        var pageCount = Math.Max(1, (filtered.Length + PageSize - 1) / PageSize);
        var currentPage = Math.Clamp(page, 1, pageCount);
        var rows = filtered.Skip((currentPage - 1) * PageSize).Take(PageSize);

        return new(
            Unread: filtered.Count(n => n.Status == "Unread"),
            Critical: filtered.Count(n => n.Status == "Unread" && n.Severity == "danger"),
            Total: store.Notifications.Length,
            Notifications: [.. rows],
            Status: currentStatus,
            FilteredCount: filtered.Length,
            Page: currentPage,
            PageCount: pageCount)
    {
        Shell = ShellFactory.Create(store, href),
    };
    }
}
