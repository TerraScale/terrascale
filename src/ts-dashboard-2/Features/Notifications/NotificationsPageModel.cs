using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Notifications;

public sealed record NotificationsPageModel(
    int Unread,
    int Critical,
    int Total,
    string LoadedAt,
    IReadOnlyList<Notification> Notifications) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static NotificationsPageModel From(DashboardStore store, string href) => new(
        Unread: store.UnreadNotifications,
        Critical: store.Notifications.Count(n => n.Status == "Unread" && n.Severity == "danger"),
        Total: store.Notifications.Length,
        LoadedAt: "2026-09-18T10:00:00Z",
        Notifications: [.. store.Notifications])
    {
        Shell = ShellFactory.Create(store, href),
    };
}
