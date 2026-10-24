using Stripe;

namespace TerraScale.Dashboard.Features.Billing;

public sealed partial class StripeBilling
{
    // Account deletion removes the organizations only the user belongs to, so their
    // subscriptions end now, exactly as organization deletion ends them: no proration,
    // no refund. A failure keeps the account; a retry reads Stripe before canceling again.
    public async Task CancelForAccountDeletionAsync(string orgId, string orgName, CancellationToken cancellationToken)
    {
        // The caller verified consent and sole ownership. Fence publication and
        // resolve an interrupted reservation before persisting the auth operation.
        // Provider failures retain the deleting binding and its exact references.
        try { await EndSubscriptionAsync(orgId, cancellationToken, authorizedDeletionPreflight: true); }
        catch (Exception error) when (error is StripeException or HttpRequestException or IOException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Account deletion could not cancel the subscription of organization {OrganizationId} ({ErrorType}: {Error})",
                orgId, error.GetType().Name, BillingPlanEndpoints.Describe(error));
            throw new AccountAccessException($"Stripe could not cancel the paid plan of {orgName}, so your account was not deleted. Try again in a few minutes.", StatusCodes.Status502BadGateway);
        }
    }
}
