namespace TerraScale.DotNet;

/// <summary>
/// Client for provider-neutral TerraScale organization billing endpoints.
/// </summary>
public sealed class TerraScaleBillingClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleBillingClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Gets the current billing account projection for an organization.
    /// </summary>
    public Task<TerraScaleBillingAccount> GetAccountAsync(
        string organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        return client.SendAsync<TerraScaleBillingAccount>(
            HttpMethod.Get,
            $"/api/v1/organizations/{Escape(organizationId)}/billing",
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Lists persisted payment-provider invoice projections for an organization.
    /// </summary>
    public Task<TerraScaleBillingInvoicePage> ListInvoicesAsync(
        string organizationId,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<TerraScaleBillingInvoicePage>(
            HttpMethod.Get,
            $"/api/v1/organizations/{Escape(organizationId)}/billing/invoices?skip={skip}&take={take}",
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Creates a payment-provider checkout session for an organization.
    /// </summary>
    public Task<TerraScaleCheckoutSession> CreateCheckoutSessionAsync(
        string organizationId,
        TerraScaleCreateCheckoutSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentNullException.ThrowIfNull(request);
        return client.SendAsync<TerraScaleCheckoutSession>(
            HttpMethod.Post,
            $"/api/v1/organizations/{Escape(organizationId)}/billing/checkout-sessions",
            request,
            cancellationToken);
    }

    /// <summary>
    /// Creates a payment-provider customer portal session for an organization.
    /// </summary>
    public Task<TerraScaleBillingPortalSession> CreatePortalSessionAsync(
        string organizationId,
        TerraScaleCreateBillingPortalSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentNullException.ThrowIfNull(request);
        return client.SendAsync<TerraScaleBillingPortalSession>(
            HttpMethod.Post,
            $"/api/v1/organizations/{Escape(organizationId)}/billing/portal-sessions",
            request,
            cancellationToken);
    }

    private static string Escape(string value) =>
        Uri.EscapeDataString(value.Trim());
}

/// <summary>
/// Organization billing account state.
/// </summary>
public sealed record TerraScaleBillingAccount(
    string BillingAccountId,
    string OrganizationId,
    string PlanId,
    string Status,
    string? BillingInterval = null,
    string Provider = "polar");

/// <summary>
/// Persisted payment-provider invoice projection returned by TerraScale billing.
/// </summary>
public sealed record TerraScaleBillingInvoice(
    string InvoiceId,
    string BillingAccountId,
    long AmountCents,
    string Currency,
    string Status,
    DateTimeOffset IssuedAt,
    string? HostedInvoiceUrl = null,
    string? InvoicePdf = null,
    DateTimeOffset? PeriodStart = null,
    DateTimeOffset? PeriodEnd = null);

/// <summary>
/// Page of persisted payment-provider invoice projections.
/// </summary>
public sealed record TerraScaleBillingInvoicePage(
    IReadOnlyList<TerraScaleBillingInvoice> Items,
    int TotalCount,
    string? NextCursor);

/// <summary>
/// Request body for creating a payment-provider checkout session.
/// </summary>
public sealed record TerraScaleCreateCheckoutSessionRequest(
    string? PriceId = null,
    string? SuccessUrl = null,
    string? CancelUrl = null,
    string? CustomerEmail = null,
    int? Quantity = null);

/// <summary>
/// Payment-provider checkout session returned by TerraScale billing.
/// </summary>
public sealed record TerraScaleCheckoutSession(
    string SessionId,
    string Url);

/// <summary>
/// Request body for creating a payment-provider customer portal session.
/// </summary>
public sealed record TerraScaleCreateBillingPortalSessionRequest(
    string? ReturnUrl = null);

/// <summary>
/// Payment-provider customer portal session returned by TerraScale billing.
/// </summary>
public sealed record TerraScaleBillingPortalSession(string Url);
