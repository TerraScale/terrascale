using System.Collections.Immutable;
using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Subscriptions;

public sealed record SubscriptionsPageModel(
    ImmutableArray<DashboardDatabase> Databases,
    SubscriptionUsageFacts Usage,
    string? AttachedViewId,
    string? AttachedDatabaseId,
    bool Online) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static SubscriptionsPageModel From(
        DashboardStore store, string href, string? viewId, string? databaseId) => new(
        Databases: store.ProjectDatabases(store.ActiveProjectId),
        Usage: store.SubscriptionUsage,
        AttachedViewId: viewId,
        AttachedDatabaseId: databaseId,
        Online: viewId is not null)
    {
        Shell = ShellFactory.Create(store, href),
    };
}
