using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Subscriptions;

public sealed class CompleteCheckoutEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/checkout/{sessionId}/complete");
        Summary("Confirms the local test payment method");
        Tags("Subscriptions");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var session = HttpContext.Request.RouteValues["sessionId"]?.ToString() ?? "";
        var redirect = store.CompletePaymentSetup(session, canceled: false);
        return Task.FromResult<IResult>(redirect is null
            ? Results.NotFound()
            : Results.Redirect(redirect, false, false));
    }
}
