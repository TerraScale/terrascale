using System.Collections.Immutable;
using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Home.Pages.Home;

public sealed record HomePageModel(
    int Databases,
    int ProjectUsers,
    int UnreadNotifications,
    int TotalNotifications,
    int ActiveCredentials,
    (string Range, ImmutableArray<WorkloadPoint> Points)[] Workloads,
    string ActivityRange,
    ImmutableArray<WorkloadPoint> ActivityPoints,
    int ActivityEvents,
    bool MonitoringStale) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static HomePageModel From(DashboardStore store, string href, string? range = null)
    {
        var workloads = store.Workloads;
        if (workloads.IsEmpty)
        {
            // Fresh accounts: v1's API returns zero-valued buckets, so the
            // activity chart renders a flat line instead of no chart.
            workloads = [.. ZeroWorkload()];
        }

        var activity = workloads.FirstOrDefault(w => w.Range == range);
        if (string.IsNullOrEmpty(activity.Range))
        {
            activity = workloads.FirstOrDefault(w => w.Range == "7d");
        }
        if (string.IsNullOrEmpty(activity.Range))
        {
            activity = workloads[0];
        }
        return new(
            Databases: store.ProjectDatabases(store.ActiveProjectId).Length,
            ProjectUsers: store.TeamMembers.Length,
            UnreadNotifications: store.UnreadNotifications,
            TotalNotifications: store.Notifications.Length,
            ActiveCredentials: store.ApiKeys.Count(k => k.IsActive),
            Workloads: [.. workloads],
            ActivityRange: activity.Range,
            ActivityPoints: activity.Points,
            ActivityEvents: (int)activity.Points.Sum(p => p.Value),
            MonitoringStale: store.MonitoringStale)
        {
            Shell = ShellFactory.Create(store, href),
        };
    }

    private static (string Range, ImmutableArray<WorkloadPoint> Points)[] ZeroWorkload() =>
    [
        ("24h", [.. Enumerable.Range(0, 24).Select(i => new WorkloadPoint(i.ToString(), 0))]),
        ("7d", [.. Enumerable.Range(0, 7).Select(i => new WorkloadPoint(i.ToString(), 0))]),
        ("30d", [.. Enumerable.Range(0, 30).Select(i => new WorkloadPoint(i.ToString(), 0))]),
    ];
}
