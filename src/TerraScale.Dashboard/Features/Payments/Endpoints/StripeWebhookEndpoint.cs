using System.Text;
using TerraScale.Dashboard;
using TerraScale.Dashboard.Features.Payments.Services;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Payments.Endpoints;

/// <summary>Receives Stripe webhook deliveries. Stripe cannot present a
/// dashboard session, so the route is anonymous and every request is
/// authenticated by the HMAC signature instead.</summary>
public sealed class StripeWebhookEndpoint(StripePaymentsService payments) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/auth/stripe/webhook");
        Summary("Projects signed Stripe billing webhooks");
        Tags("Subscriptions");
        AllowAnonymous();
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        if (!payments.Enabled)
        {
            return Results.Json(new { error = "stripe_not_configured" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        string payload;
        using (var reader = new StreamReader(HttpContext.Request.Body, Encoding.UTF8))
        {
            payload = await reader.ReadToEndAsync(ct);
        }

        var signature = HttpContext.Request.Headers["Stripe-Signature"].ToString();
        var result = await payments.ApplyWebhookAsync(payload, signature, ct);
        return result.StatusCode == StatusCodes.Status200OK
            ? Results.Json(new { received = true, outcome = result.Message })
            : Results.Json(new { error = result.Message }, statusCode: result.StatusCode);
    }
}
