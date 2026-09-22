using System.Net;
using Stripe;
using Stripe.Checkout;
using TerraScale.Dashboard.Features.Payments.Models;
using PortalSessionCreateOptions = Stripe.BillingPortal.SessionCreateOptions;
using PortalSessionService = Stripe.BillingPortal.SessionService;
using Session = Stripe.Checkout.Session;

namespace TerraScale.Dashboard.Features.Payments.Services;

/// <summary>The Stripe calls the dashboard depends on. Keeping the surface
/// narrow lets fast tests inject a deterministic provider double.</summary>
public interface IStripeBillingClient
{
    Task<StripeCustomer> CreateCustomerAsync(string email, string organizationId, string idempotencyKey, CancellationToken ct);

    Task<StripeCheckoutSession> CreateCheckoutSessionAsync(StripeCheckoutRequest request, CancellationToken ct);

    Task<StripeCheckoutSession?> GetCheckoutSessionAsync(string sessionId, CancellationToken ct);

    Task<StripePortalSession> CreatePortalSessionAsync(string customerId, Uri returnUrl, string idempotencyKey, CancellationToken ct);

    Task<StripeSubscription?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct);
}

/// <summary>
/// The official Stripe.net SDK behind the dashboard's own port. Price,
/// customer, and organization identity are resolved from trusted server state
/// and never taken from caller-supplied text; every mutation carries a
/// caller-owned idempotency key.
/// </summary>
public sealed class StripeBillingClient(StripePaymentOptions options) : IStripeBillingClient
{
    // The dashboard resolves endpoints (and this client) at startup even when
    // the sandbox is unconfigured, and StripeClient rejects an empty key. The
    // SDK client is therefore built on first real use; every call site is
    // already guarded by StripePaymentsService.Enabled.
    private readonly Lazy<SdkServices> _sdk = new(() => new SdkServices(new StripeClient(options.SecretKey)));

    private SdkServices Sdk => _sdk.Value;

    public async Task<StripeCustomer> CreateCustomerAsync(string email, string organizationId, string idempotencyKey, CancellationToken ct)
    {
        var customer = await Sdk.Customers.CreateAsync(
            new CustomerCreateOptions
            {
                Email = email,
                Metadata = new Dictionary<string, string> { ["organization_id"] = organizationId },
            },
            Request(idempotencyKey),
            ct);
        return new StripeCustomer(customer.Id);
    }

    public async Task<StripeCheckoutSession> CreateCheckoutSessionAsync(StripeCheckoutRequest request, CancellationToken ct)
    {
        var metadata = new Dictionary<string, string>
        {
            ["organization_id"] = request.OrganizationId,
            ["organization_name"] = request.OrganizationName,
            ["plan_key"] = request.PlanKey,
            ["interval"] = request.Interval,
        };
        var session = await Sdk.CheckoutSessions.CreateAsync(
            new SessionCreateOptions
            {
                Mode = "subscription",
                Customer = request.CustomerId,
                ClientReferenceId = request.OrganizationId,
                LineItems = [new SessionLineItemOptions { Price = request.PriceId, Quantity = 1 }],
                SuccessUrl = request.SuccessUrl.ToString(),
                CancelUrl = request.CancelUrl.ToString(),
                Metadata = metadata,
                SubscriptionData = new SessionSubscriptionDataOptions
                {
                    Metadata = new Dictionary<string, string>
                    {
                        ["organization_id"] = request.OrganizationId,
                        ["plan_key"] = request.PlanKey,
                    },
                },
            },
            Request(request.IdempotencyKey),
            ct);
        return Map(session);
    }

    public async Task<StripeCheckoutSession?> GetCheckoutSessionAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            return Map(await Sdk.CheckoutSessions.GetAsync(sessionId, null, null, ct));
        }
        catch (StripeException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<StripePortalSession> CreatePortalSessionAsync(string customerId, Uri returnUrl, string idempotencyKey, CancellationToken ct)
    {
        var session = await Sdk.PortalSessions.CreateAsync(
            new PortalSessionCreateOptions
            {
                Customer = customerId,
                ReturnUrl = returnUrl.ToString(),
            },
            Request(idempotencyKey),
            ct);
        return new StripePortalSession(session.Id, session.Url);
    }

    public async Task<StripeSubscription?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        try
        {
            return Map(await Sdk.Subscriptions.GetAsync(subscriptionId, null, null, ct));
        }
        catch (StripeException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static RequestOptions Request(string idempotencyKey) => new() { IdempotencyKey = idempotencyKey };

    private static StripeCheckoutSession Map(Session session) => new(
        session.Id,
        session.Url,
        session.CustomerId,
        session.SubscriptionId,
        session.Status ?? "open",
        session.PaymentStatus,
        session.ClientReferenceId,
        session.ClientSecret);

    private static StripeSubscription Map(Subscription subscription)
    {
        var item = subscription.Items?.Data?.FirstOrDefault();
        return new StripeSubscription(
            subscription.Id,
            subscription.Status ?? "unknown",
            subscription.CustomerId,
            item?.Price?.Id,
            PeriodEnd(item),
            subscription.Metadata ?? new Dictionary<string, string>());
    }

    /// <summary>Stripe moved <c>current_period_end</c> onto the subscription
    /// item, so the billed period is the first item's window.</summary>
    private static DateTimeOffset? PeriodEnd(SubscriptionItem? item) =>
        item is null || item.CurrentPeriodEnd == default
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(item.CurrentPeriodEnd, DateTimeKind.Utc));

    private sealed class SdkServices
    {
        public SdkServices(IStripeClient client)
        {
            Customers = new CustomerService(client);
            CheckoutSessions = new SessionService(client);
            Subscriptions = new SubscriptionService(client);
            PortalSessions = new PortalSessionService(client);
        }

        public CustomerService Customers { get; }

        public SessionService CheckoutSessions { get; }

        public SubscriptionService Subscriptions { get; }

        public PortalSessionService PortalSessions { get; }
    }
}
