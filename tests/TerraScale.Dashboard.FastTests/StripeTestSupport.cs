using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using TerraScale.Dashboard.Features.Payments.Models;
using TerraScale.Dashboard.Features.Payments.Services;

namespace TerraScale.Dashboard.FastTests;

/// <summary>Deterministic provider double: no socket ever leaves the test.</summary>
internal sealed class FakeStripeBillingClient : IStripeBillingClient
{
    public string CustomerId { get; set; } = "cus_test_dashboard";
    public string CheckoutUrl { get; set; } = "https://checkout.stripe.com/c/pay/cs_test_dashboard";
    public string PortalUrl { get; set; } = "https://billing.stripe.com/p/session/test_dashboard";
    public StripeCheckoutSession? Session { get; set; }
    public List<StripeCheckoutRequest> CheckoutRequests { get; } = [];
    public List<string> CustomersCreated { get; } = [];

    public Task<StripeCustomer> CreateCustomerAsync(string email, string organizationId, string idempotencyKey, CancellationToken ct)
    {
        CustomersCreated.Add(organizationId);
        return Task.FromResult(new StripeCustomer(CustomerId));
    }

    public Task<StripeCheckoutSession> CreateCheckoutSessionAsync(StripeCheckoutRequest request, CancellationToken ct)
    {
        CheckoutRequests.Add(request);
        return Task.FromResult(Session ?? new StripeCheckoutSession(
            "cs_test_dashboard",
            CheckoutUrl,
            request.CustomerId,
            "sub_test_dashboard",
            "open",
            "unpaid",
            request.OrganizationId,
            null));
    }

    public Task<StripeCheckoutSession?> GetCheckoutSessionAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult(Session);

    public Task<StripePortalSession> CreatePortalSessionAsync(string customerId, Uri returnUrl, string idempotencyKey, CancellationToken ct) =>
        Task.FromResult(new StripePortalSession("bps_test_dashboard", PortalUrl));

    public Task<StripeSubscription?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct) =>
        Task.FromResult<StripeSubscription?>(null);
}

internal static class StripeTestSupport
{
    public const string WebhookSecret = "whsec_fasttests_dashboard";

    public static StripePaymentOptions EnabledOptions(string? publicOrigin = "https://dashboard.test") => new(
        true,
        "test",
        "sk_test_fasttests",
        WebhookSecret,
        null,
        publicOrigin,
        ImmutableDictionary<string, string>.Empty
            .Add("starter:monthly", "price_starter_monthly")
            .Add("pro:monthly", "price_pro_monthly")
            .Add("business:monthly", "price_business_monthly"));

    /// <summary>Builds the exact <c>Stripe-Signature</c> header Stripe sends.</summary>
    public static string Sign(string payload, string secret = WebhookSecret, DateTimeOffset? at = null)
    {
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();
        return $"t={timestamp},v1={signature}";
    }
}
