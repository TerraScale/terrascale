using System.Collections.Immutable;
using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Home;

public sealed record HomePageModel(
    int Databases,
    int UnreadNotifications,
    int TotalNotifications,
    int ActiveCredentials,
    (string Range, ImmutableArray<WorkloadPoint> Points)[] Workloads) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static HomePageModel From(DashboardStore store, string href)
    {
        var databases = store.ProjectDatabases(store.ActiveProjectId);
        return new(
            Databases: databases.Length,
            UnreadNotifications: store.UnreadNotifications,
            TotalNotifications: store.Notifications.Length,
            ActiveCredentials: store.ApiKeys.Count(k => k.IsActive),
            Workloads: [.. store.Workloads])
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}
