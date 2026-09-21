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

internal sealed class ReceiveStripeWebhookEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/billing/webhook", HandleAsync);

    private static Task<IResult> HandleAsync(HttpRequest request, AuthOptions options, AuthStore store, TerraScaleClient sdk, CancellationToken ct)
        => StripeWebhooks.HandleAsync(request, options, store, sdk, ct);
}
