using System.Collections.Concurrent;
using System.Text.Json;
using TerraScale.Dashboard.Features.Organization.Models;
using TerraScale.Dashboard.Features.Payments.Models;

namespace TerraScale.Dashboard.Features.Payments.Services;

/// <summary>Outcome of one webhook delivery: the HTTP status Stripe should
/// receive and a short, non-sensitive reason for the logs.</summary>
public sealed record StripeWebhookResult(int StatusCode, string Message);

/// <summary>
/// Orchestrates the Stripe sandbox: hosted Checkout and Billing Portal
/// redirects plus webhook projection. Provider identifiers live only on the
/// organization; the browser never receives a price or customer id.
/// </summary>
public sealed class StripePaymentsService(
    StripePaymentOptions options,
    IStripeBillingClient client,
    DashboardStore store,
    ILogger<StripePaymentsService> logger)
{
    private readonly ConcurrentDictionary<string, byte> _processedEvents = new(StringComparer.Ordinal);

    public bool Enabled => options.Enabled;

    /// <summary>Creates (or resumes) a hosted Checkout session and returns the
    /// Stripe URL to redirect the customer to.</summary>
    public async Task<string> BeginCheckoutAsync(
        OrganizationWorld organization,
        string planKey,
        string interval,
        string returnPath,
        Uri origin,
        CancellationToken ct)
    {
        if (options.PriceId(planKey, interval) is not { Length: > 0 } priceId)
        {
            throw new StripeApiException($"No Stripe sandbox price is configured for the {planKey} plan ({interval}).", 400);
        }

        var customerId = await EnsureCustomerAsync(organization, ct);
        var successUrl = new Uri(
            $"{PaymentLinks.Absolute(origin, "/billing/checkout/return")}?return={Uri.EscapeDataString(returnPath)}&session_id={{CHECKOUT_SESSION_ID}}");
        var cancelUrl = new Uri(PaymentLinks.WithPayment(PaymentLinks.Absolute(origin, returnPath).ToString(), "canceled"));

        var session = await client.CreateCheckoutSessionAsync(
            new StripeCheckoutRequest(
                priceId,
                customerId,
                organization.Id,
                organization.ActiveOrganizationName,
                planKey,
                interval,
                successUrl,
                cancelUrl,
                CheckoutIdempotency(organization.Id, planKey, interval)),
            ct);

        organization.CheckoutSessionId = session.Id;
        organization.CheckoutReturnPath = returnPath;
        organization.PlanKey = planKey;
        return session.Url ?? throw new StripeApiException("Stripe did not return a Checkout URL.", 502);
    }

    /// <summary>Creates a Billing Portal session for the organization's Stripe
    /// customer and returns the URL to redirect to.</summary>
    public async Task<string> BeginPortalAsync(
        OrganizationWorld organization,
        string returnPath,
        Uri origin,
        CancellationToken ct)
    {
        var customerId = await EnsureCustomerAsync(organization, ct);
        var session = await client.CreatePortalSessionAsync(
            customerId,
            PaymentLinks.Absolute(origin, returnPath),
            $"portal:{organization.Id}:{returnPath}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300}",
            ct);
        return session.Url;
    }

    /// <summary>Confirms a returning Checkout session so the customer sees the
    /// paid state before the webhook lands. The webhook stays authoritative.</summary>
    public async Task ReconcileCheckoutReturnAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var session = await client.GetCheckoutSessionAsync(sessionId, ct);
            if (session is null)
            {
                return;
            }

            var organization = FindCheckoutOrganization(session);
            if (organization is null)
            {
                logger.LogWarning("Checkout session {SessionId} did not resolve to a dashboard organization.", session.Id);
                return;
            }

            if (session.Status == "complete" && session.PaymentStatus is "paid" or "no_payment_required")
            {
                Activate(organization, session, subscriptionStatus: "active");
            }        }
        catch (StripeApiException exception)
        {
            logger.LogWarning(exception, "Could not reconcile Checkout session {SessionId}.", sessionId);
        }
    }

    /// <summary>Verifies and projects one webhook delivery. Duplicate event ids
    /// are acknowledged without reapplying state.</summary>
    public Task<StripeWebhookResult> ApplyWebhookAsync(string payload, string signatureHeader, CancellationToken ct)
    {
        var verification = StripeWebhookVerifier.Verify(
            payload,
            signatureHeader,
            options.WebhookSecret,
            DateTimeOffset.UtcNow,
            StripeWebhookVerifier.DefaultTolerance);
        if (verification != StripeSignatureResult.Valid)
        {
            logger.LogWarning("Rejected a Stripe webhook delivery: {Reason}", verification);
            return Task.FromResult(new StripeWebhookResult(400, verification.ToString()));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return Task.FromResult(new StripeWebhookResult(400, "invalid_json"));
        }

        using (document)
        {
            var root = document.RootElement;
            var eventId = GetString(root, "id");
            var type = GetString(root, "type");
            if (string.IsNullOrEmpty(eventId) || string.IsNullOrEmpty(type))
            {
                return Task.FromResult(new StripeWebhookResult(400, "malformed_event"));
            }

            if (!RememberEvent(eventId))
            {
                return Task.FromResult(new StripeWebhookResult(200, "duplicate"));
            }

            if (!root.TryGetProperty("data", out var data)
                || !data.TryGetProperty("object", out var payloadObject)
                || payloadObject.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(new StripeWebhookResult(200, "ignored"));
            }

            switch (type)
            {
                case "checkout.session.completed":
                    ApplyCheckoutCompleted(payloadObject);
                    break;
                case "customer.subscription.created":
                case "customer.subscription.updated":
                case "customer.subscription.deleted":
                    ApplySubscription(payloadObject, type);
                    break;
                case "invoice.paid":
                case "invoice.payment_failed":
                    ApplyInvoice(payloadObject, type);
                    break;
                default:
                    return Task.FromResult(new StripeWebhookResult(200, "ignored"));
            }
        }

        return Task.FromResult(new StripeWebhookResult(200, "ok"));
    }

    private async Task<string> EnsureCustomerAsync(OrganizationWorld organization, CancellationToken ct)
    {
        if (organization.StripeCustomerId is { Length: > 0 } existing)
        {
            return existing;
        }

        var customer = await client.CreateCustomerAsync(store.CurrentUser.Email, organization.Id, $"customer:{organization.Id}", ct);
        organization.StripeCustomerId = customer.Id;
        return customer.Id;
    }

    private void ApplyCheckoutCompleted(JsonElement session)
    {
        var organization = FindCheckoutOrganization(session);
        if (organization is null)
        {
            logger.LogWarning("Checkout session {SessionId} did not resolve to a dashboard organization.", GetString(session, "id"));
            return;
        }

        if (GetString(session, "payment_status") is "paid" or "no_payment_required" || GetString(session, "status") == "complete")
        {
            Activate(organization, session, subscriptionStatus: "active");
        }
    }

    private void ApplySubscription(JsonElement subscription, string type)
    {
        var organization = FindOrganization(subscription);
        if (organization is null)
        {
            logger.LogWarning("Subscription {SubscriptionId} did not resolve to a dashboard organization.", GetString(subscription, "id"));
            return;
        }

        var status = type == "customer.subscription.deleted"
            ? "canceled"
            : MapSubscriptionStatus(GetString(subscription, "status"));
        if (status is not null)
        {
            organization.BillingStatus = status;
            if (status is "active" or "trialing")
            {
                organization.PaymentMethodValidated = true;
            }
        }

        if (GetString(subscription, "id") is { Length: > 0 } subscriptionId)
        {
            organization.StripeSubscriptionId = subscriptionId;
        }

        if (GetString(subscription, "customer") is { Length: > 0 } customerId)
        {
            organization.StripeCustomerId = customerId;
        }

        if (GetMetadata(subscription).TryGetValue("plan_key", out var planKey) && planKey.Length > 0)
        {
            organization.PlanKey = planKey;
        }

        if (GetLong(subscription, "current_period_end") is { } periodEnd)
        {
            organization.BillingRenewal = DateTimeOffset.FromUnixTimeSeconds(periodEnd).UtcDateTime.ToString("O");
        }
    }

    private void ApplyInvoice(JsonElement invoice, string type)
    {
        var organization = FindOrganization(invoice);
        if (organization is null)
        {
            logger.LogWarning("Invoice event did not resolve to a dashboard organization.");
            return;
        }

        organization.BillingStatus = type == "invoice.paid" ? "active" : "past_due";
        if (type == "invoice.paid")
        {
            organization.PaymentMethodValidated = true;
        }
    }

    private void Activate(OrganizationWorld organization, JsonElement session, string subscriptionStatus)
    {
        organization.BillingStatus = subscriptionStatus;
        organization.PaymentMethodValidated = true;
        organization.CheckoutSessionId = null;
        organization.CheckoutReturnPath = null;
        if (GetString(session, "customer") is { Length: > 0 } customerId)
        {
            organization.StripeCustomerId = customerId;
        }

        if (GetString(session, "subscription") is { Length: > 0 } subscriptionId)
        {
            organization.StripeSubscriptionId = subscriptionId;
        }

        if (GetMetadata(session).TryGetValue("plan_key", out var planKey) && planKey.Length > 0)
        {
            organization.PlanKey = planKey;
        }
    }

    private OrganizationWorld? FindCheckoutOrganization(JsonElement session)
    {
        if (GetMetadata(session).TryGetValue("organization_id", out var organizationId) && organizationId.Length > 0)
        {
            return store.FindOrganization(organizationId);
        }

        if (GetString(session, "client_reference_id") is { Length: > 0 } reference)
        {
            return store.FindOrganization(reference);
        }

        return GetString(session, "customer") is { Length: > 0 } customer
            ? store.FindOrganizationByCustomer(customer)
            : null;
    }

    private OrganizationWorld? FindCheckoutOrganization(StripeCheckoutSession session)
    {
        if (session.ClientReferenceId is { Length: > 0 } reference && store.FindOrganization(reference) is { } byReference)
        {
            return byReference;
        }

        return session.CustomerId is { Length: > 0 } customer
            ? store.FindOrganizationByCustomer(customer)
            : null;
    }

    private static void Activate(OrganizationWorld organization, StripeCheckoutSession session, string subscriptionStatus)
    {
        organization.BillingStatus = subscriptionStatus;
        organization.PaymentMethodValidated = true;
        organization.CheckoutSessionId = null;
        organization.CheckoutReturnPath = null;
        if (session.CustomerId is { Length: > 0 } customerId)
        {
            organization.StripeCustomerId = customerId;
        }

        if (session.SubscriptionId is { Length: > 0 } subscriptionId)
        {
            organization.StripeSubscriptionId = subscriptionId;
        }
    }

    private OrganizationWorld? FindOrganization(JsonElement payload)
    {
        if (GetMetadata(payload).TryGetValue("organization_id", out var organizationId)
            && organizationId.Length > 0
            && store.FindOrganization(organizationId) is { } byMetadata)
        {
            return byMetadata;
        }

        if (GetString(payload, "subscription") is { Length: > 0 } subscription
            && store.FindOrganizationBySubscription(subscription) is { } bySubscription)
        {
            return bySubscription;
        }

        return GetString(payload, "customer") is { Length: > 0 } customer
            ? store.FindOrganizationByCustomer(customer)
            : null;
    }

    private bool RememberEvent(string eventId)
    {
        if (!_processedEvents.TryAdd(eventId, 0))
        {
            return false;
        }

        if (_processedEvents.Count > 4096)
        {
            foreach (var key in _processedEvents.Keys.Take(_processedEvents.Count - 2048).ToList())
            {
                _processedEvents.TryRemove(key, out _);
            }
        }

        return true;
    }

    private static string CheckoutIdempotency(string organizationId, string planKey, string interval) =>
        $"checkout:{organizationId}:{planKey}:{interval}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300}";

    private static string? MapSubscriptionStatus(string? status) => status switch
    {
        "active" => "active",
        "trialing" => "trialing",
        "past_due" => "past_due",
        "unpaid" => "unpaid",
        "canceled" => "canceled",
        "incomplete" or "incomplete_expired" or "paused" => "past_due",
        _ => null,
    };

    private static Dictionary<string, string> GetMetadata(JsonElement element)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty("metadata", out var value) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                metadata[property.Name] = property.Value.GetString() ?? "";
            }
        }

        return metadata;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
            ? parsed
            : null;
}
