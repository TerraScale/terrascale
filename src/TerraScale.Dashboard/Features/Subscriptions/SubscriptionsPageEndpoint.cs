using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Subscriptions;

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
