using System.Collections.Concurrent;
using Stripe;
using TerraScale.Dashboard.Features.Organization.Models;
using TerraScale.Dashboard.Features.Payments.Models;
using Session = Stripe.Checkout.Session;

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

        var successUrl = new Uri(
            $"{PaymentLinks.Absolute(origin, "/billing/checkout/return")}?return={Uri.EscapeDataString(returnPath)}&session_id={{CHECKOUT_SESSION_ID}}");
        var cancelUrl = new Uri(PaymentLinks.WithPayment(PaymentLinks.Absolute(origin, returnPath).ToString(), "canceled"));

        return await Translate(async () =>
        {
            var customerId = await EnsureCustomerAsync(organization, ct);
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
        });
    }

    /// <summary>Creates a Billing Portal session for the organization's Stripe
    /// customer and returns the URL to redirect to.</summary>
    public async Task<string> BeginPortalAsync(
        OrganizationWorld organization,
        string returnPath,
        Uri origin,
        CancellationToken ct) =>
        await Translate(async () =>
        {
            var customerId = await EnsureCustomerAsync(organization, ct);
            var session = await client.CreatePortalSessionAsync(
                customerId,
                PaymentLinks.Absolute(origin, returnPath),
                $"portal:{organization.Id}:{returnPath}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300}",
                ct);
            return session.Url;
        });

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
            }
        }
        catch (StripeException exception)
        {
            logger.LogWarning(exception, "Could not reconcile Checkout session {SessionId}.", sessionId);
        }
    }

    /// <summary>Verifies and projects one webhook delivery. Duplicate event ids
    /// are acknowledged without reapplying state.</summary>
    public Task<StripeWebhookResult> ApplyWebhookAsync(string payload, string signatureHeader, CancellationToken ct)
    {
        if (!StripeWebhookReader.TryRead(payload, signatureHeader, options.WebhookSecret, out var stripeEvent, out var error))
        {
            logger.LogWarning("Rejected a Stripe webhook delivery: {Reason}", error);
            return Task.FromResult(new StripeWebhookResult(400, error!));
        }

        if (!RememberEvent(stripeEvent!.Id))
        {
            return Task.FromResult(new StripeWebhookResult(200, "duplicate"));
        }

        switch (stripeEvent.Data.Object)
        {
            case Session session:
                ApplyCheckoutCompleted(session);
                break;
            case Subscription subscription:
                ApplySubscription(subscription, stripeEvent.Type);
                break;
            case Invoice invoice:
                ApplyInvoice(invoice, stripeEvent.Type);
                break;
            default:
                return Task.FromResult(new StripeWebhookResult(200, "ignored"));
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

    private void ApplyCheckoutCompleted(Session session)
    {
        var organization = FindCheckoutOrganization(session);
        if (organization is null)
        {
            logger.LogWarning("Checkout session {SessionId} did not resolve to a dashboard organization.", session.Id);
            return;
        }

        if (session.PaymentStatus is "paid" or "no_payment_required" || session.Status == "complete")
        {
            Activate(organization, session, subscriptionStatus: "active");
        }
    }

    private void ApplySubscription(Subscription subscription, string type)
    {
        var organization = FindOrganization(subscription);
        if (organization is null)
        {
            logger.LogWarning("Subscription {SubscriptionId} did not resolve to a dashboard organization.", subscription.Id);
            return;
        }

        var status = type == "customer.subscription.deleted"
            ? "canceled"
            : MapSubscriptionStatus(subscription.Status);
        if (status is not null)
        {
            organization.BillingStatus = status;
            if (status is "active" or "trialing")
            {
                organization.PaymentMethodValidated = true;
            }
        }

        if (subscription.Id is { Length: > 0 } subscriptionId)
        {
            organization.StripeSubscriptionId = subscriptionId;
        }

        if (subscription.CustomerId is { Length: > 0 } customerId)
        {
            organization.StripeCustomerId = customerId;
        }

        if (SubscriptionPlanKey(subscription) is { Length: > 0 } planKey)
        {
            organization.PlanKey = planKey;
        }

        if (PeriodEnd(subscription) is { } periodEnd)
        {
            organization.BillingRenewal = periodEnd.UtcDateTime.ToString("O");
        }
    }

    private void ApplyInvoice(Invoice invoice, string type)
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

    private void Activate(OrganizationWorld organization, Session session, string subscriptionStatus)
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

        if (PlanKey(session.Metadata) is { Length: > 0 } planKey)
        {
            organization.PlanKey = planKey;
        }
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

    private OrganizationWorld? FindCheckoutOrganization(Session session)
    {
        if (OrganizationId(session.Metadata) is { Length: > 0 } organizationId
            && store.FindOrganization(organizationId) is { } byMetadata)
        {
            return byMetadata;
        }

        if (session.ClientReferenceId is { Length: > 0 } reference
            && store.FindOrganization(reference) is { } byReference)
        {
            return byReference;
        }

        return session.CustomerId is { Length: > 0 } customer
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

    private OrganizationWorld? FindOrganization(Subscription subscription)
    {
        if (OrganizationId(subscription.Metadata) is { Length: > 0 } organizationId
            && store.FindOrganization(organizationId) is { } byMetadata)
        {
            return byMetadata;
        }

        if (store.FindOrganizationBySubscription(subscription.Id) is { } bySubscription)
        {
            return bySubscription;
        }

        return subscription.CustomerId is { Length: > 0 } customer
            ? store.FindOrganizationByCustomer(customer)
            : null;
    }

    private OrganizationWorld? FindOrganization(Invoice invoice)
    {
        // Billing-reason invoices carry the subscription metadata that the
        // checkout attached; fall back to the identifiers we already stored.
        if (OrganizationId(invoice.Parent?.SubscriptionDetails?.Metadata ?? invoice.Metadata) is { Length: > 0 } organizationId
            && store.FindOrganization(organizationId) is { } byMetadata)
        {
            return byMetadata;
        }

        if (invoice.Parent?.SubscriptionDetails?.SubscriptionId is { Length: > 0 } subscription
            && store.FindOrganizationBySubscription(subscription) is { } bySubscription)
        {
            return bySubscription;
        }

        return invoice.CustomerId is { Length: > 0 } customer
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

    /// <summary>Surfaces SDK failures through the dashboard's own error type so
    /// the endpoints map every provider failure the same way.</summary>
    private static async Task<string> Translate(Func<Task<string>> call)
    {
        try
        {
            return await call();
        }
        catch (StripeException exception)
        {
            throw new StripeApiException(exception.Message, (int)exception.HttpStatusCode);
        }
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

    private static string? OrganizationId(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue("organization_id", out var value) ? value : null;

    private static string? PlanKey(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue("plan_key", out var value) ? value : null;

    private static string? SubscriptionPlanKey(Subscription subscription) => PlanKey(subscription.Metadata);

    /// <summary>Stripe moved <c>current_period_end</c> onto the subscription
    /// item, so the billed period is the first item's window.</summary>
    private static DateTimeOffset? PeriodEnd(Subscription subscription)
    {
        var item = subscription.Items?.Data?.FirstOrDefault();
        return item is null || item.CurrentPeriodEnd == default
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(item.CurrentPeriodEnd, DateTimeKind.Utc));
    }
}
