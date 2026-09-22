using TerraScale.Dashboard;
using TerraScale.Dashboard.Features.Organization.Models;
using TerraScale.Dashboard.Features.Payments.Models;
using TerraScale.Dashboard.Features.Payments.Services;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Payments.Endpoints;

/// <summary>Starts a paid subscription: the Stripe sandbox hosted Checkout when
/// configured, otherwise the in-memory local payment setup the demo uses.</summary>
public sealed class BillingCheckoutEndpoint(
    DashboardStore store,
    StripePaymentsService payments,
    StripePaymentOptions options,
    ILogger<BillingCheckoutEndpoint> logger) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/billing/checkout");
        Summary("Starts Stripe Checkout for the active organization");
        Tags("Subscriptions");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var query = HttpContext.Request.Query;
        var returnPath = PaymentLinks.NormalizeReturnPath(query["return"].FirstOrDefault(), "/usage");
        if (!payments.Enabled)
        {
            return Results.Redirect(store.BeginPaymentSetup(returnPath), false, false);
        }

        var interval = query["interval"].FirstOrDefault() == "annual" ? "annual" : "monthly";
        var organization = store.ActiveOrganization;
        var planKey = ResolvePlanKey(query["plan"].FirstOrDefault(), organization);
        try
        {
            var url = await payments.BeginCheckoutAsync(
                organization,
                planKey,
                interval,
                returnPath,
                PaymentLinks.Origin(HttpContext, options),
                ct);
            return Results.Redirect(url, false, false);
        }
        catch (StripeApiException exception)
        {
            logger.LogWarning(exception, "Stripe Checkout could not start for organization {OrganizationId}.", organization.Id);
            return Results.Redirect(Unavailable(returnPath), false, false);
        }
    }

    internal static string ResolvePlanKey(string? requested, OrganizationWorld organization)
    {
        if (StripePaymentOptions.NormalizePlan(requested) is { Length: > 0 } normalized)
        {
            return normalized;
        }

        if (StripePaymentOptions.NormalizePlan(organization.PlanKey) is { Length: > 0 } stored)
        {
            return stored;
        }

        return organization.PlanId switch
        {
            "plan_dedicated" => "business",
            "plan_scale" => "pro",
            _ => "starter",
        };
    }

    internal static string Unavailable(string returnPath) =>
        returnPath + (returnPath.Contains('?', StringComparison.Ordinal) ? '&' : '?') + "billing=stripe_unavailable";
}
