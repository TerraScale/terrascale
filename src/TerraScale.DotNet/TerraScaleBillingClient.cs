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
            TerraScaleApiRoutes.BillingAccountGet(organizationId),
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
            TerraScaleApiRoutes.BillingInvoicesList(organizationId, skip, take),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Lists publicly visible pricing plans.
    /// </summary>
    public Task<IReadOnlyList<TerraScalePricingPlan>> ListPricingPlansAsync(
        CancellationToken cancellationToken = default) =>
        client.SendAsync<IReadOnlyList<TerraScalePricingPlan>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.PricingPlansList(),
            body: null,
            cancellationToken);

    /// <summary>
    /// Lists members with delegated billing access for an organization.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleBillingAccessMember>> ListAccessAsync(
        string organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        return client.SendAsync<IReadOnlyList<TerraScaleBillingAccessMember>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.BillingAccessList(organizationId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Grants or revokes delegated billing access for an organization member.
    /// Reuse <paramref name="idempotencyKey"/> only when retrying the same request.
    /// </summary>
    public Task<TerraScaleBillingAccessMember> UpdateAccessAsync(
        string organizationId,
        string userId,
        TerraScaleUpdateBillingAccessRequest request,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(request);

        if (idempotencyKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
            if (idempotencyKey.Trim().Length > 128)
            {
                throw new ArgumentException("Idempotency key must not exceed 128 characters.", nameof(idempotencyKey));
            }
        }

        return client.SendAsync<TerraScaleBillingAccessMember>(
            HttpMethod.Put,
            TerraScaleApiRoutes.BillingAccessUpdate(organizationId, userId),
            request,
            idempotencyKey is null
                ? null
                : message =>
                {
                    message.Headers.Remove("Idempotency-Key");
                    message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.Trim());
                },
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
            TerraScaleApiRoutes.BillingCheckoutSessionsCreate(organizationId),
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
            TerraScaleApiRoutes.BillingPortalSessionsCreate(organizationId),
            request,
            cancellationToken);
    }

}

/// <summary>
/// Organization billing account state.
/// </summary>
public sealed record TerraScaleBillingAccount(
    string BillingAccountId,
    string OrganizationId,
    string PlanId,
    string Status,
    string? BillingInterval = null);

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
/// Publicly available TerraScale pricing plan.
/// </summary>
public sealed record TerraScalePricingPlan(
    string PlanId,
    string Name,
    string Currency,
    int MonthlyBaseCents,
    int AnnualBaseCents,
    IReadOnlyList<TerraScalePricingUsageMetric> IncludedUsage,
    string Key,
    string? MonthlyStripePriceId,
    string? AnnualStripePriceId,
    bool Visible,
    int MaxRegions,
    int OperationOverageCentsPerMillion,
    int StorageOverageCentsPerGbRegion);

/// <summary>
/// Included usage allowance for a public pricing plan.
/// </summary>
public sealed record TerraScalePricingUsageMetric(string Name, long Quantity, string Unit);

/// <summary>
/// Organization member with delegated billing access.
/// </summary>
public sealed record TerraScaleBillingAccessMember(
    string UserId,
    string Email,
    string DisplayName,
    string Role,
    bool Enabled,
    DateTimeOffset GrantedAt,
    string GrantedByUserId);

/// <summary>
/// Request to grant or revoke delegated billing access.
/// </summary>
public sealed record TerraScaleUpdateBillingAccessRequest(bool Enabled);

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
