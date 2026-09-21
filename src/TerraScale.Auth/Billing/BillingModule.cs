namespace TerraScale.Auth.Billing;

internal static class BillingModule
{
    internal static IEnumerable<Type> Endpoints =>
    [
        typeof(GetBillingEndpoint),
        typeof(CreateCheckoutEndpoint),
        typeof(CreateBillingPortalEndpoint),
        typeof(CheckQuotaEndpoint),
        typeof(ReconcileQuotaEndpoint),
        typeof(ReceiveStripeWebhookEndpoint),
        typeof(BillingReturnEndpoint),
        typeof(GetBillingPageEndpoint),
    ];
}
