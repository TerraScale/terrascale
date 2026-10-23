using Stripe;

namespace TerraScale.Dashboard.Features.Billing;

public sealed partial class StripeBilling
{
    // Account deletion removes the organizations only the user belongs to, so their
    // subscriptions end now: no proration, no refund, no final invoice. A failure
    // keeps the account; a retry reads Stripe's state before canceling again.
    public async Task CancelForAccountDeletionAsync(string orgId, string orgName, CancellationToken cancellationToken)
    {
        var binding = await ReadAsync(orgId, cancellationToken);
        if (binding is not { Kind: "stripe", SubscriptionId.Length: > 0 } || SubscriptionEnded(binding.Status)) return;
        try
        {
            var client = await ClientAsync(cancellationToken);
            var subscription = await client.V1.Subscriptions.GetAsync(binding.SubscriptionId, cancellationToken: cancellationToken);
            if (subscription.Id != binding.SubscriptionId || subscription.CustomerId != binding.CustomerId || subscription.Metadata.GetValueOrDefault("organization_id") != orgId)
                throw new IOException("Subscription ownership does not match.");
            if (!SubscriptionEnded(subscription.Status))
                subscription = await client.V1.Subscriptions.CancelAsync(subscription.Id, new() { InvoiceNow = false, Prorate = false },
                    Idempotent("account-deletion/" + subscription.Id), cancellationToken);
            if (!SubscriptionEnded(subscription.Status)) throw new IOException("Stripe did not cancel the subscription.");
            logger.LogInformation("Canceled subscription {SubscriptionId} of organization {OrganizationId} for account deletion", subscription.Id, orgId);
        }
        catch (Exception error) when (error is StripeException or HttpRequestException or IOException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Account deletion could not cancel subscription {SubscriptionId} of organization {OrganizationId} ({ErrorType}: {Error})",
                binding.SubscriptionId, orgId, error.GetType().Name, BillingPlanEndpoints.Describe(error));
            throw new AccountAccessException($"Stripe could not cancel the paid plan of {orgName}, so your account was not deleted. Try again in a few minutes.", StatusCodes.Status502BadGateway);
        }
    }

    private static bool SubscriptionEnded(string status) => status is "canceled" or "incomplete_expired";
}
