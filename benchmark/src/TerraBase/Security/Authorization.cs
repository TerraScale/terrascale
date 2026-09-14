namespace TerraBase.Security;

/// <summary>Operations that can be granted to a tenant principal.</summary>
public enum TenantCapability
{
    /// <summary>Read one document or collection.</summary>
    DocumentRead,

    /// <summary>Create or replace a document.</summary>
    DocumentWrite,

    /// <summary>Delete a document.</summary>
    DocumentDelete,

    /// <summary>Execute a query.</summary>
    QueryExecute,

    /// <summary>Open or continue a live subscription.</summary>
    SubscriptionRead,

    /// <summary>Read a backup artifact.</summary>
    BackupRead,

    /// <summary>Write a backup artifact.</summary>
    BackupWrite,

    /// <summary>Delete a backup artifact during delegated retention cleanup.</summary>
    BackupDelete,
}

/// <summary>An authenticated caller identity used by tenant authorization hooks.</summary>
public sealed record SecurityPrincipal
{
    /// <summary>Creates a principal with a stable subject identifier.</summary>
    public SecurityPrincipal(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("A security principal requires a subject.", nameof(subject));
        }

        Subject = subject;
    }

    /// <summary>The stable identity-provider subject.</summary>
    public string Subject { get; }
}

/// <summary>A request passed to the tenant authorization boundary.</summary>
public sealed record TenantAuthorizationRequest
{
    /// <summary>Creates an authorization request for one exact tenant.</summary>
    public TenantAuthorizationRequest(
        SecurityPrincipal principal,
        TenantId tenant,
        TenantCapability capability,
        string? resource = null)
    {
        Principal = principal ?? throw new ArgumentNullException(nameof(principal));
        tenant.EnsureValid(nameof(tenant));
        if (!Enum.IsDefined(capability))
        {
            throw new ArgumentOutOfRangeException(nameof(capability), capability, "The tenant capability is unknown.");
        }

        if (resource is not null && string.IsNullOrWhiteSpace(resource))
        {
            throw new ArgumentException("The authorization resource cannot be empty.", nameof(resource));
        }

        Tenant = tenant;
        Capability = capability;
        Resource = resource;
    }

    /// <summary>The authenticated caller.</summary>
    public SecurityPrincipal Principal { get; }

    /// <summary>The exact tenant being authorized.</summary>
    public TenantId Tenant { get; }

    /// <summary>The operation being authorized.</summary>
    public TenantCapability Capability { get; }

    /// <summary>An optional resource identifier within the tenant.</summary>
    public string? Resource { get; }
}

/// <summary>The fail-closed result returned by a tenant authorization hook.</summary>
public readonly record struct TenantAuthorizationDecision
{
    /// <summary>Creates an authorization decision.</summary>
    public TenantAuthorizationDecision(bool allowed, DateTimeOffset? expiresAt = null, string? reason = null)
    {
        if (reason is not null && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A decision reason cannot be empty.", nameof(reason));
        }

        var now = DateTimeOffset.UtcNow;
        if (allowed)
        {
            var expiry = expiresAt ?? now.Add(SecurityLimits.MaximumAuthorizationLease);
            if (expiry <= now || expiry > now.Add(SecurityLimits.MaximumAuthorizationLease))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expiresAt),
                    "Authorization leases must expire in the future and no later than 30 seconds from now.");
            }

            ExpiresAt = expiry;
        }
        else if (expiresAt is not null)
        {
            throw new ArgumentException("A denied authorization must not carry an expiry.", nameof(expiresAt));
        }

        Allowed = allowed;
        Reason = reason;
    }

    /// <summary>Whether the request is currently authorized.</summary>
    public bool Allowed { get; }

    /// <summary>The bounded authorization expiry, when allowed.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>A non-sensitive denial reason.</summary>
    public string? Reason { get; }

    /// <summary>Whether this decision can still authorize work at the supplied time.</summary>
    public bool IsValidAt(DateTimeOffset utcNow) =>
        Allowed && ExpiresAt is { } expiry && utcNow < expiry;

    /// <summary>Creates an allowed decision with a bounded expiry.</summary>
    public static TenantAuthorizationDecision AllowUntil(DateTimeOffset expiresAt) =>
        new(true, expiresAt);

    /// <summary>Creates a denied decision.</summary>
    public static TenantAuthorizationDecision Deny(string reason) =>
        new(false, reason: reason);
}

/// <summary>Hook used by the API boundary to authorize one tenant operation.</summary>
public interface ITenantAuthorizationHook
{
    /// <summary>Authorizes an exact tenant and capability for a short lease.</summary>
    ValueTask<TenantAuthorizationDecision> AuthorizeAsync(
        TenantAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Convenience methods for resource-specific capability checks.</summary>
public static class CapabilityChecks
{
    /// <summary>Checks document read access for the document's owning tenant.</summary>
    public static ValueTask<TenantAuthorizationDecision> CheckDocumentReadAsync(
        ITenantAuthorizationHook hook,
        SecurityPrincipal principal,
        DocumentKey key,
        CancellationToken cancellationToken = default) =>
        CheckAsync(hook, principal, key.Tenant, TenantCapability.DocumentRead, key.DocumentId, cancellationToken);

    /// <summary>Checks document write access for the document's owning tenant.</summary>
    public static ValueTask<TenantAuthorizationDecision> CheckDocumentWriteAsync(
        ITenantAuthorizationHook hook,
        SecurityPrincipal principal,
        DocumentKey key,
        CancellationToken cancellationToken = default) =>
        CheckAsync(hook, principal, key.Tenant, TenantCapability.DocumentWrite, key.DocumentId, cancellationToken);

    /// <summary>Checks document delete access for the document's owning tenant.</summary>
    public static ValueTask<TenantAuthorizationDecision> CheckDocumentDeleteAsync(
        ITenantAuthorizationHook hook,
        SecurityPrincipal principal,
        DocumentKey key,
        CancellationToken cancellationToken = default) =>
        CheckAsync(hook, principal, key.Tenant, TenantCapability.DocumentDelete, key.DocumentId, cancellationToken);

    /// <summary>Checks query access for one tenant.</summary>
    public static ValueTask<TenantAuthorizationDecision> CheckQueryAsync(
        ITenantAuthorizationHook hook,
        SecurityPrincipal principal,
        TenantId tenant,
        string? queryResource = null,
        CancellationToken cancellationToken = default) =>
        CheckAsync(hook, principal, tenant, TenantCapability.QueryExecute, queryResource, cancellationToken);

    /// <summary>Checks subscription access for one tenant.</summary>
    public static ValueTask<TenantAuthorizationDecision> CheckSubscriptionAsync(
        ITenantAuthorizationHook hook,
        SecurityPrincipal principal,
        TenantId tenant,
        string? subscriptionResource = null,
        CancellationToken cancellationToken = default) =>
        CheckAsync(hook, principal, tenant, TenantCapability.SubscriptionRead, subscriptionResource, cancellationToken);

    /// <summary>Checks backup access for one tenant.</summary>
    public static ValueTask<TenantAuthorizationDecision> CheckBackupAsync(
        ITenantAuthorizationHook hook,
        SecurityPrincipal principal,
        TenantId tenant,
        TenantCapability capability = TenantCapability.BackupRead,
        string? backupResource = null,
        CancellationToken cancellationToken = default)
    {
        if (capability is not (TenantCapability.BackupRead or TenantCapability.BackupWrite or TenantCapability.BackupDelete))
        {
            throw new ArgumentOutOfRangeException(nameof(capability), capability, "The capability is not a backup capability.");
        }

        return CheckAsync(hook, principal, tenant, capability, backupResource, cancellationToken);
    }

    /// <summary>Requires a decision to be allowed and unexpired.</summary>
    public static async ValueTask RequireAsync(
        ValueTask<TenantAuthorizationDecision> decision,
        CancellationToken cancellationToken = default)
    {
        var result = await decision.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.IsValidAt(DateTimeOffset.UtcNow))
        {
            throw new UnauthorizedAccessException(result.Reason ?? "The security capability is not authorized.");
        }
    }

    private static ValueTask<TenantAuthorizationDecision> CheckAsync(
        ITenantAuthorizationHook hook,
        SecurityPrincipal principal,
        TenantId tenant,
        TenantCapability capability,
        string? resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(principal);
        return hook.AuthorizeAsync(new TenantAuthorizationRequest(principal, tenant, capability, resource), cancellationToken);
    }
}

