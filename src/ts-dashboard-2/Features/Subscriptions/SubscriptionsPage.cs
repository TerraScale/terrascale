using System.Collections.Immutable;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Subscriptions;

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

public sealed class SubscriptionsPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/subscriptions");
        Summary("Renders the Subscriptions page");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var viewId = HttpContext.Request.Query["viewId"].FirstOrDefault();
        var databaseId = HttpContext.Request.Query["databaseId"].FirstOrDefault();
        return Task.FromResult<IResult>(SubscriptionsSlice.Create(
            SubscriptionsPageModel.From(store, "/subscriptions", viewId, databaseId)));
    }
}
