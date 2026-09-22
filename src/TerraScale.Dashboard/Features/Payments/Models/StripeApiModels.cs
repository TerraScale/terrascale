namespace TerraScale.Dashboard.Features.Payments.Models;

/// <summary>Server-owned inputs for a hosted Stripe Checkout subscription
/// session. Price, customer, and organization identity are resolved from
/// trusted server state, never from caller-supplied text.</summary>
public sealed record StripeCheckoutRequest(
    string PriceId,
    string CustomerId,
    string OrganizationId,
    string OrganizationName,
    string PlanKey,
    string Interval,
    Uri SuccessUrl,
    Uri CancelUrl,
    string IdempotencyKey);

public sealed record StripeCheckoutSession(
    string Id,
    string? Url,
    string? CustomerId,
    string? SubscriptionId,
    string Status,
    string? PaymentStatus,
    string? ClientReferenceId,
    string? ClientSecret);

public sealed record StripePortalSession(string Id, string Url);

public sealed record StripeCustomer(string Id);

public sealed record StripeSubscription(
    string Id,
    string Status,
    string? CustomerId,
    string? PriceId,
    DateTimeOffset? CurrentPeriodEnd,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>A provider call failed. The message is the Stripe-reported reason
/// and is safe to log; it never contains the secret key.</summary>
public sealed class StripeApiException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
