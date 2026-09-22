using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private const string ApiVersion = "2024-06-20";

    public static StripePaymentOptions EnabledOptions(string? publicOrigin = "https://dashboard.test") => new(
        true,
        "test",
        "sk_test_fasttests",
        WebhookSecret,
        publicOrigin,
        ImmutableDictionary<string, string>.Empty
            .Add("starter:monthly", "price_starter_monthly")
            .Add("starter:annual", "price_starter_annual")
            .Add("pro:monthly", "price_pro_monthly")
            .Add("pro:annual", "price_pro_annual")
            .Add("business:monthly", "price_business_monthly")
            .Add("business:annual", "price_business_annual"));

    /// <summary>Builds the exact <c>Stripe-Signature</c> header Stripe sends.</summary>
    public static string Sign(string payload, string secret = WebhookSecret, DateTimeOffset? at = null)
    {
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();
        return $"t={timestamp},v1={signature}";
    }

    /// <summary>A real-shaped <c>checkout.session.completed</c> delivery. The
    /// nested <c>object</c> discriminator is what the SDK uses to pick the
    /// typed model.</summary>
    public static string CheckoutCompletedPayload(
        string organizationId,
        string eventId = "evt_checkout_completed",
        string customerId = "cus_webhook",
        string subscriptionId = "sub_webhook",
        string planKey = "pro",
        string paymentStatus = "paid",
        string status = "complete") =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            api_version = ApiVersion,
            created = 1_700_000_000,
            livemode = false,
            pending_webhooks = 1,
            type = "checkout.session.completed",
            data = new
            {
                @object = new
                {
                    id = "cs_test_webhook",
                    @object = "checkout.session",
                    status,
                    payment_status = paymentStatus,
                    customer = customerId,
                    subscription = subscriptionId,
                    client_reference_id = organizationId,
                    metadata = new Dictionary<string, string>
                    {
                        ["organization_id"] = organizationId,
                        ["plan_key"] = planKey,
                    },
                },
            },
        });

    public static string SubscriptionPayload(
        string organizationId,
        string type = "customer.subscription.updated",
        string eventId = "evt_subscription_updated",
        string subscriptionId = "sub_existing",
        string customerId = "cus_existing",
        string status = "past_due",
        string planKey = "business",
        long? periodEndUnix = null) =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            api_version = ApiVersion,
            created = 1_700_000_000,
            livemode = false,
            pending_webhooks = 1,
            type,
            data = new
            {
                @object = new
                {
                    id = subscriptionId,
                    @object = "subscription",
                    status,
                    customer = customerId,
                    metadata = new Dictionary<string, string>
                    {
                        ["organization_id"] = organizationId,
                        ["plan_key"] = planKey,
                    },
                    items = new
                    {
                        @object = "list",
                        data = new object[]
                        {
                            new
                            {
                                id = "si_webhook",
                                @object = "subscription_item",
                                current_period_end = periodEndUnix ?? DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(),
                                price = new { id = "price_business_monthly", @object = "price" },
                            },
                        },
                    },
                },
            },
        });

    public static string InvoicePayload(
        string organizationId,
        string type = "invoice.paid",
        string eventId = "evt_invoice_paid",
        string customerId = "cus_existing") =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            api_version = ApiVersion,
            created = 1_700_000_000,
            livemode = false,
            pending_webhooks = 1,
            type,
            data = new
            {
                @object = new
                {
                    id = "in_webhook",
                    @object = "invoice",
                    customer = customerId,
                    status = type == "invoice.paid" ? "paid" : "open",
                    metadata = new Dictionary<string, string> { ["organization_id"] = organizationId },
                },
            },
        });
}
