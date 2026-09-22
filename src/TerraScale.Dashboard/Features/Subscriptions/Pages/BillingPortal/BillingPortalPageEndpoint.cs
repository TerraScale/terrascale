using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Subscriptions.Pages.BillingPortal;

public sealed class BillingPortalPageEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/billing/portal/{sessionId}");
        Summary("Renders the local development billing portal");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var sessionId = (HttpContext.Request.RouteValues["sessionId"] as string ?? "").Trim();
        return Task.FromResult<IResult>(BillingPortalSlice.Create(new BillingPortalPageModel(sessionId)));
    }
}
