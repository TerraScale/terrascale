namespace TerraScale.Auth.Connections;

using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.DotNet;

/// <summary>
/// Upstream identity connections: social/OIDC and enterprise SAML SSO. The
/// management API (sk_-gated) provisions connections per instance; the /ui
/// routes drive the actual browser sign-in flows.
/// </summary>
public static class ConnectionEndpoints
{
    private static readonly TimeSpan OidcStateTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SamlStateTtl = TimeSpan.FromMinutes(10);

    /// <summary>Plain HTTP client for upstream IdP calls; redirects are never followed.</summary>
    internal static readonly HttpClient Upstream = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>Maps Connections endpoints.</summary>
    public static void Map(WebApplication app)
    {
        // Management API (sk_ via InstanceResolver; slug must match the key's instance).
        app.MapPost("/v1/{slug}/connections", CreateAsync);
        app.MapGet("/v1/{slug}/connections", ListAsync);
        app.MapPatch("/v1/{slug}/connections/{id}", UpdateAsync);
        app.MapDelete("/v1/{slug}/connections/{id}", DeleteAsync);

        // Sign-in surface. The hosted sign-in page links to /ui/{slug}/connections/list for its
        // social/SSO buttons (the sign-in page itself is owned by the Users module).
        app.MapGet("/ui/{slug}/connections", ListPublicAsync);
        app.MapGet("/ui/{slug}/connections/list", ListPageAsync);
        app.MapGet("/ui/{slug}/connections/{connectionId}/oidc/authorize", OidcLogin.AuthorizeAsync);
        app.MapGet("/ui/{slug}/connections/{connectionId}/oidc/callback", OidcLogin.CallbackAsync);
        app.MapGet("/ui/{slug}/connections/{connectionId}/saml/login", SamlLogin.LoginAsync);
        app.MapPost("/ui/{slug}/connections/{connectionId}/saml/acs", SamlLogin.AcsAsync);
    }

    // ---- Management API ---------------------------------------------------------------

    private static async Task<IResult> CreateAsync(
        string slug, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var (body, bodyFailure) = await ReadBodyAsync<CreateConnectionBody>(request, ct);
        if (body is null)
            return bodyFailure!;
        if (string.IsNullOrWhiteSpace(body.Name))
            return ApiResults.BadRequest("invalid_request", "name is required");

        return body.Type switch
        {
            "oidc" => await CreateOidcAsync(context.Instance, body, options, store, ct),
            "saml" => await CreateSamlAsync(context.Instance, body, store, ct),
            _ => ApiResults.BadRequest("invalid_request", "type must be oidc or saml"),
        };
    }

    private static async Task<IResult> CreateOidcAsync(Instance instance, CreateConnectionBody body, AuthOptions options, AuthStore store, CancellationToken ct)
    {
        if (body.IssuerUrl is null || !Uri.TryCreate(body.IssuerUrl, UriKind.Absolute, out var issuer) || issuer.Scheme is not ("http" or "https"))
            return ApiResults.BadRequest("invalid_request", "issuer_url must be an absolute http(s) URL");
        if (string.IsNullOrWhiteSpace(body.ClientId) || string.IsNullOrWhiteSpace(body.ClientSecret))
            return ApiResults.BadRequest("invalid_request", "client_id and client_secret are required");

        // Body endpoint overrides win over discovery metadata (tests pin them this way).
        var discovery = await OidcLogin.FetchDiscoveryAsync(issuer, ct);
        var authorization = body.AuthorizationEndpoint ?? discovery?.AuthorizationEndpoint;
        var token = body.TokenEndpoint ?? discovery?.TokenEndpoint;
        if (authorization is null || token is null)
            return ApiResults.BadRequest("discovery_failed", $"could not resolve authorization and token endpoints from {issuer.ToString().TrimEnd('/')}/.well-known/openid-configuration");

        var connection = new Connection
        {
            Id = "con_" + SecureTokens.RandomToken(20),
            InstanceId = instance.Id,
            Type = "oidc",
            Name = body.Name!.Trim(),
            Enabled = true,
            OrgId = Optional(body.OrgId),
            IssuerUrl = issuer.ToString().TrimEnd('/'),
            ClientId = body.ClientId,
            ClientSecretBox = SecretBox.Encrypt(options.MasterKey, Encoding.UTF8.GetBytes(body.ClientSecret)),
            AuthorizationEndpoint = authorization,
            TokenEndpoint = token,
            UserinfoEndpoint = body.UserinfoEndpoint ?? discovery?.UserinfoEndpoint,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        await store.CreateConnectionAsync(connection, ct);
        return Results.Json(ConnectionView(connection), statusCode: 201);
    }

    private static async Task<IResult> CreateSamlAsync(Instance instance, CreateConnectionBody body, AuthStore store, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.IdpEntityId) || string.IsNullOrWhiteSpace(body.SsoRedirectUrl) || string.IsNullOrWhiteSpace(body.X509CertificatePem))
            return ApiResults.BadRequest("invalid_request", "idp_entity_id, sso_redirect_url, and x509_certificate_pem are required");
        if (!Uri.TryCreate(body.SsoRedirectUrl, UriKind.Absolute, out var sso) || sso.Scheme is not ("http" or "https"))
            return ApiResults.BadRequest("invalid_request", "sso_redirect_url must be an absolute http(s) URL");
        using var parsed = TryLoadCertificate(body.X509CertificatePem);
        if (parsed is null)
            return ApiResults.BadRequest("invalid_certificate", "x509_certificate_pem is not a parsable PEM certificate");

        var connection = new Connection
        {
            Id = "con_" + SecureTokens.RandomToken(20),
            InstanceId = instance.Id,
            Type = "saml",
            Name = body.Name!.Trim(),
            Enabled = true,
            OrgId = Optional(body.OrgId),
            IdpEntityId = body.IdpEntityId,
            SsoRedirectUrl = body.SsoRedirectUrl,
            X509CertificatePem = body.X509CertificatePem,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        await store.CreateConnectionAsync(connection, ct);
        return Results.Json(ConnectionView(connection), statusCode: 201);
    }

    private static async Task<IResult> ListAsync(string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();
        var connections = await store.ListConnectionsAsync(context.Instance.Id, ct);
        return Results.Json(new { data = connections.OrderBy(c => c.CreatedAt).Select(ConnectionView).ToList() });
    }

    private static async Task<IResult> UpdateAsync(
        string slug, string id, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var (body, bodyFailure) = await ReadBodyAsync<UpdateConnectionBody>(request, ct);
        if (body is null)
            return bodyFailure!;

        var connection = await store.GetConnectionAsync(id, ct);
        if (connection is null || connection.InstanceId != context.Instance.Id)
            return ApiResults.NotFound("connection_not_found", "no connection with this id in this instance");

        var updated = connection;
        if (body.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return ApiResults.BadRequest("invalid_request", "name must not be empty");
            updated = updated with { Name = body.Name.Trim() };
        }
        if (body.Enabled is { } enabled)
            updated = updated with { Enabled = enabled };
        if (body.ClientSecret is not null)
        {
            if (string.IsNullOrWhiteSpace(body.ClientSecret))
                return ApiResults.BadRequest("invalid_request", "client_secret must not be empty");
            updated = updated with { ClientSecretBox = SecretBox.Encrypt(options.MasterKey, Encoding.UTF8.GetBytes(body.ClientSecret)) };
        }

        await store.UpsertConnectionAsync(updated, ct);
        return Results.Json(ConnectionView(updated));
    }

    private static async Task<IResult> DeleteAsync(string slug, string id, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var connection = await store.GetConnectionAsync(id, ct);
        if (connection is null || connection.InstanceId != context.Instance.Id)
            return ApiResults.NotFound("connection_not_found", "no connection with this id in this instance");
        await store.DeleteConnectionAsync(id, ct);
        return Results.NoContent();
    }

    private static Dictionary<string, object?> ConnectionView(Connection connection) => new()
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

    // ---- Public sign-in surface --------------------------------------------------------

    private static async Task<IResult> ListPublicAsync(string slug, AuthStore store, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ApiResults.NotFound();
        var enabled = await store.ListConnectionsAsync(instance.Id, ct);
        return Results.Json(enabled.Where(c => c.Enabled).OrderBy(c => c.CreatedAt)
            .Select(c => new { id = c.Id, type = c.Type, name = c.Name }).ToList());
    }

    private static async Task<IResult> ListPageAsync(string slug, AuthStore store, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return NotFoundPage();

        var enabled = (await store.ListConnectionsAsync(instance.Id, ct)).Where(c => c.Enabled).OrderBy(c => c.CreatedAt).ToList();
        var buttons = string.Join("\n", enabled.Select(c => $"""
            <form method="get" action="{LoginPath(slug, c)}">
            <button type="submit">Continue with {Ui.Esc(c.Name)}</button>
            </form>
            """));
        var body = enabled.Count == 0
            ? """<p class="muted">No sign-in options are configured.</p>"""
            : $"<div class=\"list\">\n{buttons}\n</div>";
        return Ui.Page("Sign in", $"""
            <h1>Sign in to {Ui.Esc(instance.Name)}</h1>
            {body}
            <p class="hint"><a href="/ui/{slug}/sign-in">Back to sign in</a></p>
            """);
    }

    internal static string LoginPath(string slug, Connection connection) => connection.Type switch
    {
        "oidc" => $"/ui/{slug}/connections/{connection.Id}/oidc/authorize",
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

    private static async Task<(TBody? Body, IResult? Failure)> ReadBodyAsync<TBody>(HttpRequest request, CancellationToken ct) where TBody : class
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

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    // ---- Request bodies ------------------------------------------------------------------

    private sealed record CreateConnectionBody(
        string? Type,
        string? Name,
        [property: JsonPropertyName("org_id")] string? OrgId,
        [property: JsonPropertyName("issuer_url")] string? IssuerUrl,
        [property: JsonPropertyName("client_id")] string? ClientId,
        [property: JsonPropertyName("client_secret")] string? ClientSecret,
        // Accepted for API parity; the Connection record has no scopes field yet, so authorize
        // always requests "openid email profile". Persist when the model grows the field.
        string[]? Scopes,
        [property: JsonPropertyName("authorization_endpoint")] string? AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint,
        [property: JsonPropertyName("userinfo_endpoint")] string? UserinfoEndpoint,
        [property: JsonPropertyName("idp_entity_id")] string? IdpEntityId,
        [property: JsonPropertyName("sso_redirect_url")] string? SsoRedirectUrl,
        [property: JsonPropertyName("x509_certificate_pem")] string? X509CertificatePem);

    private sealed record UpdateConnectionBody(
        bool? Enabled,
        string? Name,
        [property: JsonPropertyName("client_secret")] string? ClientSecret);
}
