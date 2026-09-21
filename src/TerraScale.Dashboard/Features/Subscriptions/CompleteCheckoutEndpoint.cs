using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Subscriptions;

public sealed class CompleteCheckoutEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/checkout/{sessionId}/complete");
        Summary("Confirms the local test payment method");
        Tags("Subscriptions");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var returnTo = HttpContext.Request.HasFormContentType
            ? (await HttpContext.Request.ReadFormAsync(ct))["returnTo"].ToString()
            : null;
        return Results.Redirect(SafeRedirect.WithPaymentResult(returnTo, "success"), false, false);
    }
}
