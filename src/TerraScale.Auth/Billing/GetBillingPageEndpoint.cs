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

internal sealed class GetBillingPageEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/billing", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string? key, AuthStore store, CancellationToken ct)
    {
        if (key is null
            || await store.FindApiKeyAsync(SecureTokens.Sha256Hex(key), ct) is not { } apiKey
            || apiKey.Revoked
            || await store.GetInstanceAsync(apiKey.InstanceId, ct) is not { } instance
            || instance.Slug != slug)
            return Ui.Page("Billing", "<h1>Not authorized</h1><p class=\"error\">A valid management key is required (?key=sk_...).</p>", status: 401);

        var state = await BillingState.CurrentAsync(store, instance.Id, ct);
        var count = await BillingState.CountUsersAsync(store, instance.Id, state.Quota + 1, ct);
        var customerId = instance.StripeCustomerId ?? state.Subscription?.StripeCustomerId;
        var renews = state.Subscription?.CurrentPeriodEnd is { } end
            ? DateTimeOffset.FromUnixTimeSeconds(end).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "—";
        return Ui.Page("Billing", $"""
            <h1>Plan: {Ui.Esc(state.Plan)}</h1>
            <div class="list">
              <div class="row"><span>Status</span><span>{Ui.Esc(state.Status)}</span></div>
              <div class="row"><span>Users</span><span>{count} of {state.Quota}</span></div>
              <div class="row"><span>Stripe customer</span><span>{Ui.Esc(customerId ?? "—")}</span></div>
              <div class="row"><span>Renews</span><span>{renews}</span></div>
            </div>
            """);
    }
}
