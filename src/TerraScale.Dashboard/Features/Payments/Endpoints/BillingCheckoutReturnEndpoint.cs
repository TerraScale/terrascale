using TerraScale.Dashboard;
using TerraScale.Dashboard.Features.Payments.Services;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Payments.Endpoints;

/// <summary>Stripe's Checkout success redirect. Confirms the session so the
/// customer sees the paid state immediately, then returns to the page that
/// started the checkout. The signed webhook remains the authoritative path.</summary>
public sealed class BillingCheckoutReturnEndpoint(
    StripePaymentsService payments) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/billing/checkout/return");
        Summary("Confirms a returned Stripe Checkout session");
        Tags("Subscriptions");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var query = HttpContext.Request.Query;
        var returnPath = PaymentLinks.NormalizeReturnPath(query["return"].FirstOrDefault(), "/usage");
        if (payments.Enabled && query["session_id"].FirstOrDefault() is { Length: > 0 } sessionId)
        {
            await payments.ReconcileCheckoutReturnAsync(sessionId, ct);
        }

        return Results.Redirect(PaymentLinks.WithPayment(returnPath, "success"), false, false);
    }
}
