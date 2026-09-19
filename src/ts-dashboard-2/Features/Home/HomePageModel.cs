using System.Collections.Immutable;
using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Home;

public sealed record HomePageModel(
    int Databases,
    int UnreadNotifications,
    int TotalNotifications,
    int ActiveCredentials,
    (string Range, ImmutableArray<WorkloadPoint> Points)[] Workloads,
    string ActivityRange,
    ImmutableArray<WorkloadPoint> ActivityPoints,
    int ActivityEvents) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static HomePageModel From(DashboardStore store, string href, string? range = null)
    {
        var workloads = store.Workloads;
        var activity = workloads.FirstOrDefault(w => w.Range == range);
        if (string.IsNullOrEmpty(activity.Range))
        {
            activity = workloads[0];
        }
        return new(
            Databases: store.ProjectDatabases(store.ActiveProjectId).Length,
            UnreadNotifications: store.UnreadNotifications,
            TotalNotifications: store.Notifications.Length,
            ActiveCredentials: store.ApiKeys.Count(k => k.IsActive),
            Workloads: [.. workloads],
            ActivityRange: activity.Range,
            ActivityPoints: activity.Points,
            ActivityEvents: (int)activity.Points.Sum(p => p.Value))
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}
