using System.Net;
using System.Text.Json;

namespace TerraScale.DotNet;

/// <summary>
/// Maps ts-auth error responses onto <see cref="TerraScaleAuthException"/>. The
/// management API uses the nested envelope <c>{"error":{code,message}}</c>;
/// the OAuth endpoints use the flat RFC 6749 shape
/// <c>{"error":"code","error_description":"message"}</c>.
/// </summary>
internal static class TerraScaleAuthErrors
{
    internal static async Task<TerraScaleAuthException> CreateAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var code = await ReadErrorCodeAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return new TerraScaleAuthException(
            response.StatusCode,
            $"ts-auth returned HTTP {(int)response.StatusCode}.",
            code,
            ResolveRetryAfter(response));
    }

    /// <summary>Resolves Retry-After in both forms (delta seconds and HTTP
    /// date), clamped to zero, so date-only headers are not dropped.</summary>
    internal static TimeSpan? ResolveRetryAfter(HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (response?.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    private const int MaximumErrorBytes = 64 * 1024;

    private static async Task<string?> ReadErrorCodeAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null || content.Headers.ContentLength > MaximumErrorBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaximumErrorBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length > MaximumErrorBytes) return null;

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error)) return null;
            var code = error.ValueKind == JsonValueKind.String ? error.GetString()
                : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var nested) && nested.ValueKind == JsonValueKind.String
                    ? nested.GetString() : null;
            return code is not null && KnownErrorCodes.TryGetValue(code, out var known) ? known : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Fixed error identities from ts-auth management/feature contracts and
    // the OAuth error vocabulary. No producer-supplied free text is retained.
    private static readonly HashSet<string> KnownErrorCodes = new(StringComparer.Ordinal)
    {
        "access_denied",
        "account_unavailable",
        "already_a_member",
        "api_key_not_found",
        "assertion_replayed",
        "authorization_pending",
        "billing_not_configured",
        "bootstrap_key_conflict",
        "checkout_in_progress",
        "client_changed",
        "connection_exists",
        "connection_not_found",
        "credential_exists",
        "delivery_changed",
        "directory_changed",
        "directory_deletion_in_progress",
        "directory_not_found",
        "discovery_failed",
        "domain_exists",
        "domain_not_found",
        "email_domain_mismatch",
        "email_not_verified",
        "email_taken",
        "event_in_progress",
        "expired_token",
        "factor_changed",
        "factor_not_found",
        "forbidden",
        "idempotency_key_required",
        "idempotency_key_reused",
        "identifier_taken",
        "instance_exists",
        "instance_not_found",
        "insufficient_scope",
        "internal_error",
        "invalid_certificate",
        "invalid_challenge",
        "invalid_client",
        "invalid_code",
        "invalid_domain",
        "invalid_email",
        "invalid_factor",
        "invalid_grant",
        "invalid_idempotency_key",
        "invalid_json",
        "invalid_logo_url",
        "invalid_mfa_token",
        "invalid_password",
        "invalid_placeholder",
        "invalid_plan",
        "invalid_request",
        "invalid_role",
        "invalid_scope",
        "invalid_slug",
        "invalid_theme_color",
        "invalid_token",
        "invitation_not_found",
        "invitation_not_pending",
        "membership_changed",
        "membership_not_found",
        "no_customer",
        "not_found",
        "organization_changed",
        "organization_deletion_in_progress",
        "organization_exists",
        "organization_not_found",
        "origin_rejected",
        "passkey_not_found",
        "payload_too_large",
        "rate_limited",
        "registration_failed",
        "reserved_claim",
        "server_error",
        "session_not_found",
        "session_required",
        "slow_down",
        "stripe_error",
        "subscription_exists",
        "template_changed",
        "template_exists",
        "template_not_found",
        "temporarily_unavailable",
        "token_collision",
        "unauthorized",
        "unauthorized_client",
        "unknown_credential",
        "unsupported_grant_type",
        "unsupported_response_type",
        "user_not_found",
        "username_taken",
    };
}
