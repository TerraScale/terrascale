using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Subscriptions;

public sealed class CheckoutPageEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/checkout/{sessionId}");
        Summary("Renders the local development checkout page");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var sessionId = HttpContext.Request.RouteValues["sessionId"] as string ?? "";
        // The wizard that started checkout supplies ?return= so the payment
        // result resumes it; unknown or off-origin values fall back to onboarding.
        var returnTo = SafeRedirect.LocalPath(HttpContext.Request.Query["return"].FirstOrDefault());
        var model = new CheckoutPageModel(sessionId.Trim(), returnTo);
        return Task.FromResult<IResult>(CheckoutSlice.Create(model));
    }
}
