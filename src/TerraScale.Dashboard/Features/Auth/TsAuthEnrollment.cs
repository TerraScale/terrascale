using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth;

public sealed partial class TsAuthGateway
{
    public async Task<string> SignUpAsync(string email, string password, string ip, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ip)) throw new ArgumentException("Client address is required.", nameof(ip));
        var credentials = await identity.ReadAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(AuthOrigin, $"/{Uri.EscapeDataString(credentials.AuthInstanceSlug)}/headless/v1/sign-up"));
        Authenticate(request, credentials);
        request.Content = JsonContent.Create(new { email, password, ip_address = ip });
        using var response = await http.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) throw RateLimited(response);
        using var document = await PlatformJson.ReadAsync(response.Content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Only known validation text is shown. Never expose vendor error
            // details that could include submitted credentials.
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) ThrowPasswordPolicy(document.RootElement);
            response.EnsureSuccessStatusCode();
        }
        if (!document.RootElement.TryGetProperty("pending_token", out var token) || token.ValueKind != JsonValueKind.String || token.GetString() is not { Length: > 0 and <= 4096 } value)
            throw new HttpRequestException("ts-auth answered without a signup token.");
        return value;
    }

    public Task<SessionRecord> VerifySignUpAsync(string email, string code, string pendingToken, string ip, CancellationToken cancellationToken) =>
        GrantAsync("urn:terrascale:params:oauth:grant-type:email-code", email, "code", code, ip, cancellationToken, pendingToken);

    public async Task<string> AuthorizationUrlAsync(OidcFlow flow, bool promptLogin, CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        var origin = AuthOrigin;
        var metadata = await Client(credentials, origin).Oidc.GetDiscoveryDocumentAsync(cancellationToken);
        var endpoint = TrustedEndpoint(metadata.AuthorizationEndpoint, origin, credentials.AuthInstanceSlug);
        var fields = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = credentials.AuthClientId,
            ["redirect_uri"] = flow.RedirectUri,
            ["scope"] = "openid email profile offline_access",
            ["state"] = flow.State,
            ["nonce"] = flow.Nonce,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(flow.Verifier))),
            ["code_challenge_method"] = "S256"
        };
        if (promptLogin) fields["prompt"] = "login";
        return endpoint + "?" + string.Join("&", fields.Select(field => Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value)));
    }

    public async Task<SessionRecord> ExchangeAsync(string code, OidcFlow flow, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 4096) throw new SignInRejectedException();
        var credentials = await identity.ReadAsync(cancellationToken);
        var origin = AuthOrigin;
        TerraScaleAuthTokenResponse tokens;
        try
        {
            tokens = await Client(credentials, origin).Oidc.ExchangeAuthorizationCodeAsync(
                credentials.AuthClientId, credentials.AuthClientSecret, code, flow.RedirectUri, flow.Verifier, cancellationToken);
        }
        catch (TerraScaleAuthException error) when (error.ErrorCode == "invalid_grant") { throw new SignInRejectedException(); }
        return await SessionAsync(tokens, credentials, origin, flow.Nonce, cancellationToken);
    }

    public async Task<SessionRecord> RefreshAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.RefreshToken)) throw new SignInRejectedException();
        var credentials = await identity.ReadAsync(cancellationToken);
        var origin = AuthOrigin;
        var client = Client(credentials, origin);
        TerraScaleAuthTokenResponse tokens;
        try
        {
            tokens = await client.Oidc.RefreshTokenAsync(credentials.AuthClientId, credentials.AuthClientSecret, session.RefreshToken, cancellationToken);
        }
        catch (TerraScaleAuthException error) when (error.ErrorCode == "invalid_grant" || error.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized)
        { throw new SignInRejectedException(); }
        if (tokens.ExpiresIn is <= 0 or > 86400 || !string.Equals(tokens.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(tokens.AccessToken))
            throw new SignInRejectedException();
        // OIDC allows refresh answers without an ID token. A new ID token must
        // still prove the original subject, and userinfo remains mandatory.
        if (tokens.IdToken is { Length: > 0 })
        {
            var metadata = await client.Oidc.GetDiscoveryDocumentAsync(cancellationToken);
            _ = TrustedIssuer(metadata.Issuer, origin, credentials.AuthInstanceSlug);
            var claims = AuthIdToken.Validate(tokens.IdToken, metadata.Issuer, credentials.AuthClientId, null,
                await client.Oidc.GetJwksAsync(cancellationToken), time.GetUtcNow());
            if (claims.Subject != session.UserId) throw new SignInRejectedException();
        }
        var user = await UserInfoAsync(client, tokens.AccessToken, cancellationToken);
        if (user.Subject != session.UserId || user.EmailVerified != true || user.Email?.Trim().ToLowerInvariant() != session.Email) throw new SignInRejectedException();
        var refreshed = session with
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(tokens.RefreshToken) ? session.RefreshToken : tokens.RefreshToken,
            ExpiresAt = time.GetUtcNow().AddSeconds(tokens.ExpiresIn),
            RefreshLease = "",
            RefreshLeaseUntil = null
        };
        if (!await IsActiveAsync(refreshed, cancellationToken)) throw new SignInRejectedException();
        return refreshed;
    }

    public async Task<string> EndSessionUrlAsync(string dashboardOrigin, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(dashboardOrigin, UriKind.Absolute, out var dashboard) || dashboard.UserInfo.Length != 0 ||
            dashboard.AbsolutePath != "/" || dashboard.Query.Length != 0 || dashboard.Fragment.Length != 0 ||
            dashboard.Scheme is not ("https" or "http")) throw new ArgumentException("Invalid dashboard origin.", nameof(dashboardOrigin));
        try
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            var origin = AuthOrigin;
            var metadata = await Client(credentials, origin).Oidc.GetDiscoveryDocumentAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(metadata.EndSessionEndpoint)) return "/login?prompt=login";
            var endpoint = TrustedEndpoint(metadata.EndSessionEndpoint, origin, credentials.AuthInstanceSlug);
            return endpoint + "?client_id=" + Uri.EscapeDataString(credentials.AuthClientId) +
                "&post_logout_redirect_uri=" + Uri.EscapeDataString(new Uri(dashboard, "/login").AbsoluteUri);
        }
        catch (HttpRequestException) { return "/login?prompt=login"; }
    }

    private async Task<SessionRecord> SessionAsync(TerraScaleAuthTokenResponse tokens, DashboardCredentials credentials, Uri origin, string? nonce, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tokens.AccessToken) || tokens.ExpiresIn is <= 0 or > 86400 ||
            !string.Equals(tokens.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase)) throw new SignInRejectedException();
        var client = Client(credentials, origin);
        var metadata = await client.Oidc.GetDiscoveryDocumentAsync(cancellationToken);
        _ = TrustedIssuer(metadata.Issuer, origin, credentials.AuthInstanceSlug);
        if (!metadata.IdTokenSigningAlgValuesSupported.Contains("ES256", StringComparer.Ordinal)) throw new SignInRejectedException();
        var keys = await client.Oidc.GetJwksAsync(cancellationToken);
        var claims = AuthIdToken.Validate(tokens.IdToken, metadata.Issuer, credentials.AuthClientId, nonce, keys, time.GetUtcNow());
        var user = await UserInfoAsync(client, tokens.AccessToken, cancellationToken);
        if (user.Subject != claims.Subject || user.EmailVerified != true || user.Email?.Trim().ToLowerInvariant() != claims.Email) throw new SignInRejectedException();
        var session = new SessionRecord(claims.Subject, claims.Name, claims.Email, tokens.AccessToken, tokens.RefreshToken, time.GetUtcNow().AddSeconds(tokens.ExpiresIn));
        if (!await IsActiveAsync(session, cancellationToken)) throw new SignInRejectedException();
        return session;
    }

    private static string TrustedIssuer(string issuer, Uri origin, string slug)
    {
        var expected = new Uri(origin, "/" + Uri.EscapeDataString(slug));
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsoluteUri != expected.AbsoluteUri)
            throw new SignInRejectedException();
        return uri.AbsoluteUri;
    }

    private static string TrustedEndpoint(string endpoint, Uri origin, string slug)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host != origin.Host ||
            (uri.AbsolutePath != "/" + Uri.EscapeDataString(slug) && !uri.AbsolutePath.StartsWith("/" + Uri.EscapeDataString(slug) + "/", StringComparison.Ordinal)))
            throw new HttpRequestException("ts-auth published an invalid OIDC endpoint.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static async Task<TerraScaleAuthUserInfo> UserInfoAsync(TerraScaleAuthClient client, string accessToken, CancellationToken cancellationToken)
    {
        try { return await client.Oidc.GetUserInfoAsync(accessToken, cancellationToken); }
        catch (TerraScaleAuthException error) when (error.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        { throw new SignInRejectedException(); }
    }

    private static AuthRequestRejectedException RateLimited(HttpResponseMessage response)
    {
        var seconds = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : 60;
        seconds = Math.Clamp(seconds, 1, 3600);
        return new($"Too many attempts. Please wait {seconds} seconds and try again.", statusCode: 429, retryAfter: seconds);
    }

    private static void ThrowPasswordPolicy(JsonElement body)
    {
        if (!body.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String || code.GetString() != "invalid_password") return;
        var message = error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        if (message is not { Length: > 0 and <= 200 } || message.Any(char.IsControl)) message = "Choose a different password.";
        throw new AuthRequestRejectedException(message, "password");
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
