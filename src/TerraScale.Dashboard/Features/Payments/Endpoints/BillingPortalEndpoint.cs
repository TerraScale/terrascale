using TerraScale.Dashboard;
using TerraScale.Dashboard.Features.Payments.Models;
using TerraScale.Dashboard.Features.Payments.Services;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Payments.Endpoints;

/// <summary>Opens the Stripe sandbox Billing Portal for the active
/// organization, falling back to the local demo portal when Stripe is off.</summary>
public sealed class BillingPortalEndpoint(
    DashboardStore store,
    StripePaymentsService payments,
    StripePaymentOptions options,
    ILogger<BillingPortalEndpoint> logger) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/billing/portal");
        Summary("Opens the Stripe Billing Portal for the active organization");
        Tags("Subscriptions");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var query = HttpContext.Request.Query;
        var returnPath = PaymentLinks.NormalizeReturnPath(query["return"].FirstOrDefault(), "/usage");
        if (!payments.Enabled)
        {
            return Results.Redirect("/billing/portal/usage_portal", false, false);
        }

        var organization = store.ActiveOrganization;
        try
        {
            var url = await payments.BeginPortalAsync(
                organization,
                returnPath,
                PaymentLinks.Origin(HttpContext, options),
                ct);
            return Results.Redirect(url, false, false);
        }
        catch (StripeApiException exception)
        {
            logger.LogWarning(exception, "Stripe Billing Portal could not start for organization {OrganizationId}.", organization.Id);
            return Results.Redirect(BillingCheckoutEndpoint.Unavailable(returnPath), false, false);
        }
    }
}
