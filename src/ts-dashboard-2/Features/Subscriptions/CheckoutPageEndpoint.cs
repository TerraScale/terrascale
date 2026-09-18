using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Subscriptions;

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
        var model = new CheckoutPageModel(sessionId.Trim());
        return Task.FromResult<IResult>(CheckoutSlice.Create(model));
    }
}
