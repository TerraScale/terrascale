using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Billing.Webhooks
{
    public sealed class AuthStripeWebhookEndpoint(StripeBilling billing) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/auth/stripe/webhook");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(StripeWebhookHandling.HandleAsync(HttpContext, billing));
    }
}
