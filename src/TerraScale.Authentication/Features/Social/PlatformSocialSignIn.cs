using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Features.Social;

/// <summary>Authorization-code OAuth with S256 PKCE, browser binding and durable single-use state.</summary>
public sealed class PlatformSocialSignIn(AuthOptions options, AuthStore store, Users users,
    Sessions sessions, TotpCredentials mfa, IHttpClientFactory clients, TimeProvider clock)
{
    /// <summary>The named backchannel; tests replace only its HTTP transport.</summary>
    public const string HttpClientName = "platform-social-sign-in";
    private const string Purpose = "social-sign-in";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Starts platform sign-in using only the platform's credentials.</summary>
    public async Task<IResult> StartAsync(HttpContext context, string provider, string? next, CancellationToken cancellationToken)
    {
        var tenant = SignInFlow.Tenant(context);
        if (this.Credentials(tenant, provider) is not { } credential)
        {
            return Results.NotFound();
        }

        var browser = Tokens.RandomToken(43);
        var verifier = Tokens.RandomToken(64);
        var state = SecretBox.Seal(options.MasterKey, JsonSerializer.SerializeToUtf8Bytes(
            new PlatformSocialState(tenant, provider, credential.ClientId, Tokens.Sha256Hex(SocialProviders.Open(options, tenant, credential)), verifier, Tokens.Sha256Hex(browser), SignInFlow.SafeNext(context, next)), PlatformSocialJson.Default.PlatformSocialState), Purpose);
        var hash = Tokens.Sha256Hex(state);
        await store.CreateChallengeAsync(tenant, Purpose, hash, tenant, clock.GetUtcNow() + Lifetime, cancellationToken);
        context.Response.Cookies.Append(Cookie(hash), browser, CookieOptions());
        context.Response.Headers.CacheControl = "no-store";
        var authorization = provider == SocialProviders.GitHub ? "https://github.com/login/oauth/authorize" : "https://accounts.google.com/o/oauth2/v2/auth";
        return Results.Redirect(QueryHelpers.AddQueryString(authorization, new Dictionary<string, string?>
        {
            ["client_id"] = credential.ClientId,
            ["redirect_uri"] = this.Callback(provider).AbsoluteUri,
            ["response_type"] = "code",
            ["scope"] = provider == SocialProviders.GitHub ? "read:user user:email" : "openid email profile",
            ["state"] = state,
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
        }));
    }

    /// <summary>Verifies the browser, consumes state once, then exchanges the code and opens a fresh session.</summary>
    public async Task<IResult> CompleteAsync(HttpContext context, string tenant, string provider, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var query = context.Request.Query;
        if (query["state"] is not [{ Length: > 0 and <= 8192 } state] || query["code"] is not [{ Length: > 0 and <= 2048 } code]
            || query.ContainsKey("error") || this.Credentials(tenant, provider) is not { } credential)
        {
            return Refused(context, tenant);
        }

        PlatformSocialState? payload;
        try
        {
            payload = JsonSerializer.Deserialize(SecretBox.Open(options.MasterKey, state, Purpose), PlatformSocialJson.Default.PlatformSocialState);
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            return Refused(context, tenant);
        }

        var hash = Tokens.Sha256Hex(state);
        if (payload is null || payload.Tenant != tenant || payload.Provider != provider || payload.ClientId != credential.ClientId
            || payload.CredentialHash != Tokens.Sha256Hex(SocialProviders.Open(options, tenant, credential))
            || context.Request.Cookies[Cookie(hash)] is not { Length: 43 } browser
            || !Tokens.FixedTimeEquals(payload.BrowserHash, Tokens.Sha256Hex(browser)))
        {
            return Refused(context, tenant);
        }

        context.Response.Cookies.Delete(Cookie(hash), CookieOptions());
        if (await store.ConsumeChallengeAsync(tenant, Purpose, hash, tenant, cancellationToken) is not { Binding: { } binding })
        {
            return Refused(context, tenant);
        }

        (string Subject, string Email)? identity;
        try
        {
            identity = await this.ExchangeAsync(tenant, credential, code, payload.Verifier, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Refused(context, tenant);
        }

        if (identity is not { } verified || Users.NormalizeEmail(verified.Email) is not { } email)
        {
            return Refused(context, tenant);
        }

        // The authority comes from these fixed, trusted backchannels, never from user-supplied claims.
        var providerIdentity = new LinkedProvider(provider, provider == SocialProviders.Google ? "https://accounts.google.com" : "https://github.com", credential.ClientId, verified.Subject);
        // Only a bound subject or a new account resolves; an email collision refuses without touching that account.
        if (await users.FromProviderAsync(tenant, providerIdentity, email, allowCreation: true, cancellationToken) is not { } resolved)
        {
            return Refused(context, tenant);
        }

        // An active local factor gets the hosted second step; its sealed challenge carries the consumed callback binding.
        var user = resolved with { AuthenticationBindings = [.. resolved.AuthenticationBindings, binding] };
        var next = SignInFlow.SafeNext(context, payload.Next);
        if (await SignInFlow.SecondFactorAsync(context, mfa, user, next, cancellationToken) is { } factor)
        {
            return factor;
        }

        if (await SignInFlow.OpenSessionAsync(sessions, user, cancellationToken) is not { } session)
        {
            return Refused(context, tenant);
        }

        return await SignInFlow.SignedInAsync(context, sessions, session, next, cancellationToken);
    }

    private SocialProviderRecord? Credentials(string tenant, string provider) =>
        tenant != OidcIssuer.InstanceId || provider is not (SocialProviders.GitHub or SocialProviders.Google) ? null
        : options.SocialClients.FirstOrDefault(item => item.Provider == provider);

    private Uri Callback(string provider) => new(options.PublicOrigin, $"signin/{provider}");

    private async Task<(string Subject, string Email)?> ExchangeAsync(string tenant, SocialProviderRecord credential, string code, string verifier, CancellationToken cancellationToken)
    {
        var github = credential.Provider == SocialProviders.GitHub;
        using var client = clients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, github ? "https://github.com/login/oauth/access_token" : "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["client_id"] = credential.ClientId,
                ["client_secret"] = SocialProviders.Open(options, tenant, credential),
                ["redirect_uri"] = this.Callback(credential.Provider).AbsoluteUri,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var tokenResponse = await client.SendAsync(request, cancellationToken);
        tokenResponse.EnsureSuccessStatusCode();
        using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
        if (!token.RootElement.TryGetProperty("access_token", out var access) || access.GetString() is not { Length: > 0 and <= 8192 } accessToken)
        {
            return null;
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TerraScale-Auth/1.0");
        using var userResponse = await client.GetAsync(github ? "https://api.github.com/user" : "https://openidconnect.googleapis.com/v1/userinfo", cancellationToken);
        userResponse.EnsureSuccessStatusCode();
        using var info = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync(cancellationToken));
        if (!github)
        {
            return info.RootElement.TryGetProperty("email_verified", out var verified) && verified.ValueKind == JsonValueKind.True
                && info.RootElement.TryGetProperty("sub", out var subject) && subject.GetString() is { Length: > 0 and <= 256 } sub
                && info.RootElement.TryGetProperty("email", out var email) && email.GetString() is { } address
                ? (sub, address) : null;
        }

        if (!info.RootElement.TryGetProperty("id", out var id) || !id.TryGetInt64(out var number) || number <= 0)
        {
            return null;
        }

        using var emailsResponse = await client.GetAsync("https://api.github.com/user/emails", cancellationToken);
        emailsResponse.EnsureSuccessStatusCode();
        using var emails = JsonDocument.Parse(await emailsResponse.Content.ReadAsStringAsync(cancellationToken));
        // Require a verified primary address. GitHub's public profile email is neither required nor trusted.
        foreach (var item in emails.RootElement.EnumerateArray())
        {
            if (item.GetProperty("primary").ValueKind == JsonValueKind.True && item.GetProperty("verified").ValueKind == JsonValueKind.True
                && item.GetProperty("email").GetString() is { } email)
            {
                return (number.ToString(System.Globalization.CultureInfo.InvariantCulture), email);
            }
        }

        return null;
    }

    private static string Cookie(string hash) => "__Host-tsauth_social_" + hash;

    private static CookieOptions CookieOptions() => new()
    {
        Path = "/",
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        MaxAge = Lifetime,
        IsEssential = true,
    };

    private static IResult Refused(HttpContext context, string tenant) => SignInPageModel.Render(context,
        new SignInPageModel(SignInStep.Email, tenant, Error: "Sign-in could not be completed. Try again from the application or use your existing sign-in method.")
        { ErrorTitle = "Provider sign-in failed" }, StatusCodes.Status401Unauthorized);
}

internal sealed record PlatformSocialState(string Tenant, string Provider, string ClientId, string CredentialHash, string Verifier, string BrowserHash, string? Next);

[JsonSerializable(typeof(PlatformSocialState))]
[JsonSerializable(typeof(LinkedProvidersBody))]
[JsonSerializable(typeof(UnlinkProviderRequest))]
internal sealed partial class PlatformSocialJson : JsonSerializerContext;
