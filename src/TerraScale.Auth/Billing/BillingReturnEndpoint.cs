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

internal sealed class BillingReturnEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/billing/return", Handle);

    private static IResult Handle(string slug, string? status)
        => Ui.Page("Billing", status == "success"
                ? "<h1>Subscription confirmed</h1><p class=\"success\">Your subscription is now active.</p>"
                : "<h1>Checkout canceled</h1><p class=\"error\">The checkout was canceled; nothing was charged.</p>");
}
