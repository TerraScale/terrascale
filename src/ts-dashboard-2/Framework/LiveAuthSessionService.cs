using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerraScale.Dashboard2;

/// <summary>A validated ts-auth sign-in held by one opaque dashboard cookie.</summary>
public sealed record LiveAuthSession(
    string Subject,
    string Email,
    string DisplayName,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessExpiresAt)
{
    public LiveAuthSession WithTokens(string accessToken, string refreshToken, DateTimeOffset expiresAt) =>
        this with { AccessToken = accessToken, RefreshToken = refreshToken, AccessExpiresAt = expiresAt };
}

public sealed record LiveAuthSignInResult(LiveAuthSession? Session, string? Error)
{
    public bool Ok => Session is not null;
}

/// <summary>Server-side driver for ts-auth (the SDK's auth service).
///
/// Sign-in keeps the dashboard's email+password form: the service POSTs the
/// ts-auth hosted sign-in form, walks the OIDC authorize step with the session
/// cookie (PKCE S256, confidential client), exchanges the code for tokens, and
/// stores the pair behind an opaque dashboard cookie. Access tokens are
/// validated through RFC 7662 introspection (60s positive cache) and refreshed
/// with the rotation-aware refresh grant. Logout revokes the refresh token.</summary>
public sealed class LiveAuthSessionService(DashboardSecurityOptions security)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan IntrospectionCache = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, LiveAuthSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _introspectedAt = new(StringComparer.Ordinal);

    /// <summary>Mints the opaque dashboard cookie value for a validated session.</summary>
    public string IssueCookie(LiveAuthSession session)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _sessions[token] = session;
        return token;
    }

    public LiveAuthSession? Lookup(string? cookieToken) =>
        cookieToken is not null && _sessions.TryGetValue(cookieToken, out var session) ? session : null;

    public void Drop(string? cookieToken)
    {
        if (cookieToken is not null)
        {
            _sessions.TryRemove(cookieToken, out _);
        }
    }

    /// <summary>Signs in with ts-auth: hosted form → authorize (PKCE) → token exchange.</summary>
    public async Task<LiveAuthSignInResult> SignInAsync(string email, string password, CancellationToken ct)
    {
        var identity = security.Identity!;
        using var http = CreateClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email, ["password"] = password });
        using var signIn = await http.PostAsync($"/ui/{identity.InstanceSlug}/sign-in", content, ct);
        if (signIn.StatusCode != HttpStatusCode.Redirect)
        {
            return new(null, "Incorrect email or password.");
        }

        // The hosted session cookie now authorizes the OIDC authorize step.
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var redirectUri = $"{identity.PublicOrigin.ToString().TrimEnd('/')}/api/v1/auth/oidc/callback";
        var authorizeQuery = $"response_type=code&client_id={Uri.EscapeDataString(identity.ClientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&scope={Uri.EscapeDataString("openid profile email")}&state={Uri.EscapeDataString(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant())}"
            + $"&code_challenge={challenge}&code_challenge_method=S256";
        using var authorize = await http.GetAsync($"/{identity.InstanceSlug}/oauth/authorize?{authorizeQuery}", ct);
        if (authorize.StatusCode != HttpStatusCode.Redirect || authorize.Headers.Location is not { } location)
        {
            return new(null, "The identity provider did not issue an authorization code.");
        }

        var code = ParseQuery(location.Query)["code"];
        if (code is null)
        {
            return new(null, "The identity provider did not issue an authorization code.");
        }

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{identity.ClientId}:{identity.ClientSecret}"));
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, $"/{identity.InstanceSlug}/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }),
        };
        tokenRequest.Headers.TryAddWithoutValidation("Authorization", $"Basic {credentials}");
        using var tokenResponse = await http.SendAsync(tokenRequest, ct);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            return new(null, "The identity provider rejected the token exchange.");
        }

        await using var body = await tokenResponse.Content.ReadAsStreamAsync(ct);
        var tokens = await JsonSerializer.DeserializeAsync<TokenResponse>(body, Json, ct);
        if (tokens is not { AccessToken.Length: > 0 } || tokens.RefreshToken is not { Length: > 0 })
        {
            return new(null, "The identity provider returned an incomplete token response.");
        }

        using var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, $"/{identity.InstanceSlug}/oauth/userinfo");
        userInfoRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {tokens.AccessToken}");
        using var userInfoResponse = await http.SendAsync(userInfoRequest, ct);
        if (!userInfoResponse.IsSuccessStatusCode)
        {
            return new(null, "The identity provider rejected the access token.");
        }

        await using var claimsBody = await userInfoResponse.Content.ReadAsStreamAsync(ct);
        var claims = await JsonSerializer.DeserializeAsync<UserInfoResponse>(claimsBody, Json, ct);
        if (claims is not { Subject.Length: > 0 })
        {
            return new(null, "The identity provider returned an incomplete identity.");
        }

        var session = new LiveAuthSession(
            claims.Subject,
            claims.Email ?? email,
            string.IsNullOrWhiteSpace(claims.Name) ? email : claims.Name,
            tokens.AccessToken,
            tokens.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, tokens.ExpiresIn)));
        return new(session, null);
    }

    /// <summary>Creates the ts-auth user through the instance management API
    /// (the same surface the SDK's TerraScaleAuthUsersClient drives).</summary>
    public async Task<string?> RegisterAsync(string email, string password, string displayName, CancellationToken ct)
    {
        var identity = security.Identity!;
        using var http = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/{identity.InstanceSlug}/users")
        {
            Content = new JsonContent(new Dictionary<string, string?>
            {
                ["email"] = email,
                ["password"] = password,
                ["name"] = displayName,
            }),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {identity.ManagementApiKey}");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return "An account with this email already exists.";
        }

        if (!response.IsSuccessStatusCode)
        {
            return "The identity provider could not create the account.";
        }

        return null;
    }

    /// <summary>True when the session's access token is (or can be refreshed to)
    /// active at the identity provider. Introspection is cached positively for
    /// one minute to keep the per-request cost off the IdP.</summary>
    public async Task<bool> EnsureValidAsync(LiveAuthSession session, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (session.AccessExpiresAt - TimeSpan.FromMinutes(1) <= now && !await RefreshAsync(session, ct))
        {
            return false;
        }

        if (_introspectedAt.TryGetValue(session.AccessToken, out var seenAt) && now - seenAt < IntrospectionCache)
        {
            return true;
        }

        var identity = security.Identity!;
        using var http = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, identity.IntrospectionEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = session.AccessToken }),
        };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{identity.ClientId}:{identity.ClientSecret}"));
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {credentials}");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var introspection = await JsonSerializer.DeserializeAsync<IntrospectionResponse>(body, Json, ct);
        if (introspection is not { Active: true })
        {
            _introspectedAt.TryRemove(session.AccessToken, out _);
            return false;
        }

        _introspectedAt[session.AccessToken] = now;
        return true;
    }

    /// <summary>Rotates the refresh token (ts-auth rotates on every use).</summary>
    public async Task<bool> RefreshAsync(LiveAuthSession session, CancellationToken ct)
    {
        var identity = security.Identity!;
        using var http = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{identity.InstanceSlug}/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = identity.ClientId,
                ["client_secret"] = identity.ClientSecret,
                ["refresh_token"] = session.RefreshToken,
            }),
        };
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var tokens = await JsonSerializer.DeserializeAsync<TokenResponse>(body, Json, ct);
        if (tokens is not { AccessToken.Length: > 0 } || tokens.RefreshToken is not { Length: > 0 })
        {
            return false;
        }

        session = session.WithTokens(tokens.AccessToken, tokens.RefreshToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, tokens.ExpiresIn)));
        foreach (var pair in _sessions.Where(p => p.Value.Subject == session.Subject).ToList())
        {
            _sessions[pair.Key] = session;
        }

        return true;
    }

    /// <summary>Revokes the refresh token family and forgets the session.</summary>
    public async Task RevokeAsync(LiveAuthSession session, CancellationToken ct)
    {
        var identity = security.Identity!;
        using var http = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{identity.InstanceSlug}/oauth/revoke")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = identity.ClientId,
                ["client_secret"] = identity.ClientSecret,
                ["token"] = session.RefreshToken,
            }),
        };
        await http.SendAsync(request, ct);
    }

    private HttpClient CreateClient()
    {
        var identity = security.Identity!;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        return new HttpClient(handler) { BaseAddress = identity.Origin, Timeout = TimeSpan.FromSeconds(15) };
    }

    private static Dictionary<string, string?> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null);

    private sealed record TokenResponse(
        string AccessToken,
        string? RefreshToken,
        long ExpiresIn);

    private sealed record UserInfoResponse(
        string Subject,
        string? Email,
        string? Name);

    private sealed record IntrospectionResponse(bool Active);

    private sealed class JsonContent(Dictionary<string, string?> values) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await JsonSerializer.SerializeAsync(stream, values, Json);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
