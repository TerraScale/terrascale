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

internal sealed class CreateCheckoutEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/billing/checkout", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, HttpRequest request, InstanceResolver resolver, AuthOptions options, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var (body, failure) = await ReadJsonAsync<CheckoutBody>(request, ct);
        if (failure is not null)
            return failure;
        if (body?.Plan is not (Plans.Pro or Plans.Enterprise))
            return ApiResults.BadRequest("invalid_plan", "plan must be \"pro\" or \"enterprise\"");

        var priceId = body.Plan == Plans.Pro ? options.PricePro : options.PriceEnterprise;
        if (options.StripeSecretKey is null || priceId is null)
            return ApiResults.Error(503, "billing_not_configured", "Stripe billing is not configured for this deployment");

        var origin = options.PublicOrigin.ToString().TrimEnd('/');
        JsonElement session;
        try
        {
            session = await StripeClient.PostAsync(options, "/v1/checkout/sessions", new Dictionary<string, string>
            {
                ["mode"] = "subscription",
                ["line_items[0][price]"] = priceId,
                ["line_items[0][quantity]"] = "1",
                ["success_url"] = $"{origin}/ui/{slug}/billing/return?status=success",
                ["cancel_url"] = $"{origin}/ui/{slug}/billing/return?status=cancel",
                ["client_reference_id"] = context.Instance.Id,
                ["metadata[instance_id]"] = context.Instance.Id,
            }, ct);
        }
        catch (StripeApiException exception)
        {
            return ApiResults.Error(502, "stripe_error", $"Stripe rejected the checkout session request (HTTP {exception.StatusCode}).");
        }
        catch (Exception exception) when (exception is StripeResponseException or HttpRequestException)
        {
            // Stripe unreachable, or an interceptor answered 200 with a non-JSON body.
            return ApiResults.Error(502, "stripe_error", "Stripe was unreachable or returned an unusable response.");
        }

        // Stripe returns url today; the session id is the fallback for flows without one.
        var url = Str(session, "url") ?? Str(session, "id");
        return Results.Ok(new { checkout_url = url });
    }

    private sealed record CheckoutBody(string? Plan);
}
