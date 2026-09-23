namespace TerraScale.Auth.Connections;

using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.DotNet;

internal static class ConnectionFlow
{
    internal static readonly TimeSpan OidcStateTtl = TimeSpan.FromMinutes(10);

    internal static readonly TimeSpan SamlStateTtl = TimeSpan.FromMinutes(10);

    /// <summary>Plain HTTP client for upstream IdP calls; redirects are never followed.</summary>
    internal static readonly HttpClient Upstream = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    internal static Dictionary<string, object?> ConnectionView(Connection connection) => new()
    {
        ["id"] = connection.Id,
        ["type"] = connection.Type,
        ["name"] = connection.Name,
        ["enabled"] = connection.Enabled,
        ["org_id"] = connection.OrgId,
        ["issuer_url"] = connection.IssuerUrl,
        ["client_id"] = connection.ClientId,
        ["authorization_endpoint"] = connection.AuthorizationEndpoint,
        ["token_endpoint"] = connection.TokenEndpoint,
        ["userinfo_endpoint"] = connection.UserinfoEndpoint,
        ["idp_entity_id"] = connection.IdpEntityId,
        ["sso_redirect_url"] = connection.SsoRedirectUrl,
        ["sp_entity_id"] = connection.SpEntityId,
        ["created_at"] = connection.CreatedAt,
        // client_secret never leaves the store except as an encrypted box, which is not serialized either.
    };

    internal static string LoginPath(string slug, Connection connection) => connection.Type switch
    {
        "oidc" => $"/ui/{slug}/connections/{connection.Id}/oidc/authorize",
        "github" => $"/ui/{slug}/connections/{connection.Id}/github/authorize",
        _ => $"/ui/{slug}/connections/{connection.Id}/saml/login",
    };

    // ---- Shared flow plumbing ----------------------------------------------------------

    /// <summary>Loads a live connection of the expected type for the instance; disabled/foreign/wrong-type all collapse to null (404).</summary>
    internal static async Task<Connection?> FindConnectionAsync(AuthStore store, string instanceId, string connectionId, string type, CancellationToken ct)
    {
        var connection = await store.GetConnectionAsync(connectionId, ct);
        return connection is not null && connection.InstanceId == instanceId && connection.Type == type && connection.Enabled
            ? connection
            : null;
    }

    /// <summary>
    /// Mints a one-time state token. <see cref="VerificationToken"/> has no metadata field, so
    /// <see cref="VerificationToken.UserId"/> carries the connection id this state belongs to, and
    /// the SAML flow additionally smuggles the AuthnRequest id in the record id ("vrf_&lt;rand&gt;:&lt;requestId&gt;")
    /// so the ACS can validate InResponseTo.
    /// </summary>
    internal static async Task<string> MintStateAsync(AuthStore store, string instanceId, string connectionId, string purpose, string? requestId, TimeSpan ttl, CancellationToken ct)
    {
        var token = SecureTokens.RandomToken(40);
        var now = DateTimeOffset.UtcNow;
        var id = "vrf_" + SecureTokens.RandomToken(20);
        if (requestId is not null)
            id += ":" + requestId;
        await store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = id,
            InstanceId = instanceId,
            UserId = connectionId,
            Purpose = purpose,
            TokenKey = SecureTokens.Sha256Hex(token),
            ExpiresAt = now.Add(ttl).ToUnixTimeSeconds(),
            CreatedAt = now.ToUnixTimeSeconds(),
        }, ct);
        return token;
    }

    /// <summary>Single-use consume: instance, purpose, connection (UserId), expiry, and consumed
    /// flag, then atomically marks consumed (If-Match CAS — of two concurrent consumes only one wins).</summary>
    internal static Task<VerificationToken?> ConsumeStateAsync(AuthStore store, Instance instance, string connectionId, string token, string purpose, CancellationToken ct) =>
        store.ConsumeVerificationTokenAsync(SecureTokens.Sha256Hex(token), record =>
            record.InstanceId == instance.Id
            && record.Purpose == purpose
            && record.UserId == connectionId
            && !record.Consumed
            && record.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ct);

    /// <summary>Find-or-create by email; null when the identifier is unusable or the account is banned/deleted.</summary>
    internal static async Task<User?> FindOrCreateUserAsync(AuthStore store, SessionService sessions, Instance instance, string email, bool emailVerified, string? name, CancellationToken ct)
    {
        if (!SessionService.IsValidEmail(email))
            return null;
        var normalized = SessionService.NormalizeEmail(email);
        var existing = await store.FindUserByEmailAsync(instance.Id, normalized, ct);
        if (existing is not null)
            return existing.Deleted || existing.Banned ? null : existing;

        try
        {
            var created = await sessions.CreateUserAsync(instance.Id, normalized, username: null, password: null, name, ct);
            var verified = created with { EmailVerified = emailVerified, UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            await store.UpsertUserAsync(verified, ct);
            return verified;
        }
        catch (QuotaExceededException)
        {
            // At the plan's ceiling a new identity cannot be minted; existing users above never
            // reach this line (the find returns them first).
            return null;
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            // Concurrent sign-in created the user between find and create; fall back to it.
            var raced = await store.FindUserByEmailAsync(instance.Id, normalized, ct);
            return raced is not null && !raced.Deleted && !raced.Banned ? raced : null;
        }
    }

    /// <summary>Session cookie + LastSignInAt + the user.signed_in audit event.</summary>
    internal static async Task SignInAsync(AuthStore store, SessionService sessions, HttpContext context, Instance instance, User user, string method, CancellationToken ct)
    {
        var (_, token) = await sessions.CreateSessionAsync(
            instance.Id, user.Id,
            context.Request.Headers.UserAgent.ToString(),
            context.Connection.RemoteIpAddress?.ToString(), ct);
        context.Response.Cookies.Append(SessionCookies.Name, token, SessionCookies.Options);
        await store.UpsertUserAsync(user with { LastSignInAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, ct);
        await store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instance.Id,
            Type = "user.signed_in",
            ActorId = user.Id,
            Data = new Dictionary<string, object> { ["user_id"] = user.Id, ["method"] = method },
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
    }

    internal static TimeSpan OidcStateLifetime => OidcStateTtl;

    internal static TimeSpan SamlStateLifetime => SamlStateTtl;

    internal static IResult NotFoundPage() =>
        Ui.Page("Not found", """
            <h1>Page not found</h1>
            <p class="muted">No ts-auth instance or connection lives at this address.</p>
            <p class="hint"><a href="/">Start over</a></p>
            """, status: 404);

    internal static IResult ErrorPage(string slug, string message, int status = 400) =>
        Ui.Page("Sign-in failed", $"""
            <h1>Sign-in failed</h1>
            <div class="error">{Ui.Esc(message)}</div>
            <p class="hint"><a href="/ui/{slug}/sign-in">Back to sign in</a></p>
            """, status: status);

    internal static async Task<(TBody? Body, IResult? Failure)> ReadBodyAsync<TBody>(HttpRequest request, CancellationToken ct) where TBody : class
    {
        try
        {
            var body = await request.ReadFromJsonAsync<TBody>(ct);
            return body is null
                ? (null, ApiResults.BadRequest("invalid_request", "a JSON body is required"))
                : (body, null);
        }
        catch (JsonException)
        {
            return (null, ApiResults.BadRequest("invalid_json", "the request body is not valid JSON"));
        }
    }

    /// <summary>Decodes a single CERTIFICATE PEM block; null when unparseable. This framework's
    /// X509CertificateLoader takes DER bytes only, so the armor is stripped here.</summary>
    internal static X509Certificate2? TryLoadCertificate(string pem)
    {
        try
        {
            var base64 = string.Concat(pem
                .Replace("-----BEGIN CERTIFICATE-----", "", StringComparison.Ordinal)
                .Replace("-----END CERTIFICATE-----", "", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
