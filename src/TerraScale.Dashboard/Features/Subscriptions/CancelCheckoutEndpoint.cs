using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Subscriptions;

public sealed class CancelCheckoutEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/checkout/{sessionId}/cancel");
        Summary("Cancels the local checkout session");
        Tags("Subscriptions");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var session = HttpContext.Request.RouteValues["sessionId"]?.ToString() ?? "";
        var redirect = store.CompletePaymentSetup(session, canceled: true);
        return Task.FromResult<IResult>(redirect is null
            ? Results.NotFound()
            : Results.Redirect(redirect, false, false));
    }
}
