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

internal sealed class GetBillingEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/billing", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var state = await BillingState.CurrentAsync(store, context.Instance.Id, ct);
        // Display-only count: nothing past the quota matters, so the walk stops at quota+1.
        var userCount = await BillingState.CountUsersAsync(store, context.Instance.Id, state.Quota + 1, ct);
        return Results.Ok(new
        {
            plan = state.Plan,
            status = state.Status,
            stripe_customer_id = context.Instance.StripeCustomerId ?? state.Subscription?.StripeCustomerId,
            stripe_subscription_id = state.Subscription?.StripeSubscriptionId,
            current_period_end = state.Subscription?.CurrentPeriodEnd,
            user_count = userCount,
            quota = state.Quota,
        });
    }
}
