using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Subscriptions;

public sealed class CancelCheckoutEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/checkout/{sessionId}/cancel");
        Summary("Cancels the local checkout session");
        Tags("Subscriptions");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var returnTo = HttpContext.Request.HasFormContentType
            ? (await HttpContext.Request.ReadFormAsync(ct))["returnTo"].ToString()
            : null;
        return Results.Redirect(SafeRedirect.WithPaymentResult(returnTo, "canceled"), false, false);
    }
}
