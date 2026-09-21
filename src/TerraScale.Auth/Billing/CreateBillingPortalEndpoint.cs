namespace TerraScale.Auth.Billing;

using System.Globalization;
using System.Text.Json.Serialization;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Billing.BillingSupport;

internal sealed class CreateBillingPortalEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/billing/portal", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        if (options.StripeSecretKey is null)
            return ApiResults.Error(503, "billing_not_configured", "Stripe billing is not configured for this deployment");
        if (context.Instance.StripeCustomerId is not { } customer)
            return ApiResults.BadRequest("no_customer", "this instance has no Stripe customer yet; complete a checkout first");

        var origin = options.PublicOrigin.ToString().TrimEnd('/');
        JsonElement portal;
        try
        {
            portal = await StripeClient.PostAsync(options, "/v1/billing_portal/sessions", new Dictionary<string, string>
            {
                ["customer"] = customer,
                ["return_url"] = $"{origin}/ui/{slug}/account",
            }, ct);
        }
        catch (StripeApiException exception)
        {
            return ApiResults.Error(502, "stripe_error", $"Stripe rejected the portal session request (HTTP {exception.StatusCode}).");
        }
        catch (Exception exception) when (exception is StripeResponseException or HttpRequestException)
        {
            return ApiResults.Error(502, "stripe_error", "Stripe was unreachable or returned an unusable response.");
        }
        return Results.Ok(new { portal_url = Str(portal, "url") });
    }
}
