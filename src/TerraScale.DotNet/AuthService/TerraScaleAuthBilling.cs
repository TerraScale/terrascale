using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// Plan, quota, and Stripe checkout/portal operations for one instance.
/// Stripe endpoints fail with <c>billing_not_configured</c> when the instance
/// has no Stripe key or prices configured.
/// </summary>
public sealed class TerraScaleAuthBillingClient(TerraScaleAuthClient client)
{
    /// <summary>Gets the plan, subscription status, seat usage, and quota.</summary>
    public Task<TerraScaleAuthBillingOverview> GetOverviewAsync(CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/billing"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthBillingOverview,
            cancellationToken);

    /// <summary>
    /// Creates a Stripe checkout session for <paramref name="plan"/>
    /// (<c>pro</c> or <c>enterprise</c>); any other value fails with
    /// <c>invalid_plan</c>, and Stripe-side failures surface as <c>stripe_error</c>.
    /// </summary>
    public Task<TerraScaleAuthCheckout> CreateCheckoutAsync(string plan, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plan);
        return client.SendJsonAsync<TerraScaleAuthCreateCheckout, TerraScaleAuthCheckout>(
            HttpMethod.Post,
            client.ManagementRoute("/billing/checkout"),
            new TerraScaleAuthCreateCheckout(plan),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCreateCheckout,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCheckout,
            cancellationToken);
    }

    /// <summary>
    /// Creates a Stripe billing-portal session for the instance's customer;
    /// fails with <c>no_customer</c> when no Stripe customer exists yet.
    /// </summary>
    public Task<TerraScaleAuthBillingPortal> CreatePortalSessionAsync(CancellationToken cancellationToken = default) =>
        client.SendJsonAsync<TerraScaleAuthEmptyBody, TerraScaleAuthBillingPortal>(
            HttpMethod.Post,
            client.ManagementRoute("/billing/portal"),
            TerraScaleAuthEmptyBody.Instance,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthEmptyBody,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthBillingPortal,
            cancellationToken);

    /// <summary>
    /// Asks whether one more seat fits given <paramref name="currentUsers"/>
    /// existing users, without mutating anything.
    /// </summary>
    public Task<TerraScaleAuthQuotaDecision> EnforceQuotaAsync(int currentUsers, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync<TerraScaleAuthEnforceQuota, TerraScaleAuthQuotaDecision>(
            HttpMethod.Post,
            client.ManagementRoute("/billing/enforce-check"),
            new TerraScaleAuthEnforceQuota(currentUsers),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthEnforceQuota,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthQuotaDecision,
            cancellationToken);

    /// <summary>
    /// Idempotent quota repair that removes only orphaned seat claims and
    /// reports how many claims were examined, removed, and retained.
    /// </summary>
    public Task<TerraScaleAuthQuotaReconciliation> ReconcileQuotaAsync(CancellationToken cancellationToken = default) =>
        client.PostJsonAsync(
            client.ManagementRoute("/billing/quota/reconcile"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthQuotaReconciliation,
            cancellationToken);
}

/// <summary>The instance's plan, subscription, and seat usage.</summary>
public sealed record TerraScaleAuthBillingOverview(
    [property: JsonPropertyName("plan")] string Plan,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("stripe_customer_id")] string? StripeCustomerId,
    [property: JsonPropertyName("stripe_subscription_id")] string? StripeSubscriptionId,
    [property: JsonPropertyName("current_period_end")] long? CurrentPeriodEnd,
    [property: JsonPropertyName("user_count")] int UserCount,
    [property: JsonPropertyName("quota")] int Quota);

/// <summary>A Stripe checkout session URL.</summary>
public sealed record TerraScaleAuthCheckout(
    [property: JsonPropertyName("checkout_url")] string CheckoutUrl);

/// <summary>A Stripe billing-portal session URL.</summary>
public sealed record TerraScaleAuthBillingPortal(
    [property: JsonPropertyName("portal_url")] string PortalUrl);

/// <summary>Whether a seat assignment fits the current plan quota.</summary>
public sealed record TerraScaleAuthQuotaDecision(
    [property: JsonPropertyName("allowed")] bool Allowed,
    [property: JsonPropertyName("plan")] string Plan,
    [property: JsonPropertyName("quota")] int Quota);

/// <summary>The outcome of a seat-claim reconciliation pass.</summary>
public sealed record TerraScaleAuthQuotaReconciliation(
    [property: JsonPropertyName("claims_examined")] int ClaimsExamined,
    [property: JsonPropertyName("claims_removed")] int ClaimsRemoved,
    [property: JsonPropertyName("claims_retained")] int ClaimsRetained);

internal sealed record TerraScaleAuthCreateCheckout(
    [property: JsonPropertyName("plan")] string Plan);

internal sealed record TerraScaleAuthEnforceQuota(
    [property: JsonPropertyName("current_users")] int CurrentUsers);
