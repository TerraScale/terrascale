using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using ZLogger;

namespace TerraScale.Authentication.Features.Sso;

/// <summary>The browser's pending SSO sign-in, sealed into a short-lived cookie.</summary>
/// <param name="State">The OAuth state.</param>
/// <param name="Nonce">The ID token's nonce.</param>
/// <param name="Verifier">The PKCE code verifier.</param>
/// <param name="Domain">The connection.</param>
/// <param name="Issuer">The issuer the request went to.</param>
/// <param name="Next">The validated page to resume.</param>
/// <param name="ExpiresAt">When the sign-in lapses.</param>
public sealed record SsoState(string State, string Nonce, string Verifier, string Domain, string Issuer, string? Next, DateTimeOffset ExpiresAt);

/// <summary>
/// Platform sign-in through an organization's OpenID Connect identity provider: authorization code with PKCE, state and
/// nonce, the ID token checked against the issuer's keys, and its email in the connection's verified domain. A first
/// sign-in creates the account and joins the organization with the connection's default role.
/// </summary>
public sealed class SsoSignIn(SsoConnections connections, Users users, PlatformDirectory platform, AuthOptions options, IHttpClientFactory clients, TimeProvider clock, ILogger<SsoSignIn> logger)
{
    /// <summary>The page that starts an SSO sign-in.</summary>
    public const string StartPath = $"/ui/{OidcIssuer.Slug}/sso";

    /// <summary>The redirect URI identity providers return to.</summary>
    public const string CallbackPath = SsoConnections.CallbackPath;

    /// <summary>The pending sign-in's cookie, scoped to the SSO pages.</summary>
    public const string Cookie = "tsauth_sso";

    /// <summary>How long a started sign-in may take at the identity provider.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private const string StateContext = "ts-auth org sso state";
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>The SSO start page for <paramref name="email"/>, resuming <paramref name="next"/>.</summary>
    public static string StartUrl(string email, string? next) =>
        QueryHelpers.AddQueryString(StartPath, next is null ? new Dictionary<string, string?> { ["email"] = email } : new Dictionary<string, string?> { ["email"] = email, ["next"] = next });

    /// <summary>
    /// Whether <paramref name="email"/> must sign in through its organization's identity provider: its domain is verified
    /// and it is no owner of that organization (owners keep email-code and password sign-in to recover a broken connection).
    /// </summary>
    public async Task<bool> RequiredAsync(string? email, CancellationToken cancellationToken) =>
        await connections.EnforcedAsync(email, cancellationToken).ConfigureAwait(false) is { } connection
        && (await users.FindByEmailAsync(PlatformDirectory.Instance, email!, cancellationToken).ConfigureAwait(false) is not { } user
            || await platform.RoleAsync(connection.OrganizationId, user.Id, cancellationToken).ConfigureAwait(false) != Roles.Owner);

    /// <summary>The identity provider's authorization URL for <paramref name="email"/>, and the sealed state to set; null without an enforced connection.</summary>
    public async Task<(string Location, string State)?> StartAsync(string email, string? next, CancellationToken cancellationToken)
    {
        if (await connections.EnforcedAsync(email, cancellationToken).ConfigureAwait(false) is not { Protocol: SsoConnections.Oidc, Issuer: { } issuer } connection
            || await connections.DiscoverAsync(issuer, cancellationToken).ConfigureAwait(false) is not { } discovery)
        {
            return null;
        }

        var state = new SsoState(Tokens.RandomToken(43), Tokens.RandomToken(43), Tokens.RandomToken(64), connection.Domain, issuer, next, clock.GetUtcNow() + Lifetime);
        var location = QueryHelpers.AddQueryString(discovery.AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = connection.ClientId,
            ["redirect_uri"] = this.RedirectUri,
            ["scope"] = "openid email profile",
            ["state"] = state.State,
            ["nonce"] = state.Nonce,
            ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(state.Verifier))),
            ["code_challenge_method"] = "S256",
            ["login_hint"] = email,
        });
        return (location, SecretBox.Seal(options.MasterKey, JsonSerializer.SerializeToUtf8Bytes(state, SsoStateJson.Default.SsoState), StateContext));
    }

    /// <summary>The pending sign-in sealed in <paramref name="cookie"/> when it is unexpired and matches <paramref name="state"/>.</summary>
    public SsoState? Pending(string? cookie, string? state)
    {
        try
        {
            return cookie is { Length: > 0 and <= 4096 } && state is { Length: > 0 }
                && JsonSerializer.Deserialize(SecretBox.Open(options.MasterKey, cookie, StateContext), SsoStateJson.Default.SsoState) is { } pending
                && pending.ExpiresAt > clock.GetUtcNow() && Tokens.FixedTimeEquals(pending.State, state)
                    ? pending
                    : null;
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Redeems <paramref name="code"/> at the identity provider and returns the signed-in account (created and joined
    /// on first use), or null when the response, the token or its email fails any check.
    /// </summary>
    public async Task<UserRecord?> CompleteAsync(SsoState pending, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (await connections.ForDomainAsync(pending.Domain, cancellationToken).ConfigureAwait(false) is not { Value: { VerifiedAt: not null, Protocol: SsoConnections.Oidc } connection }
            || connection.Issuer != pending.Issuer || await platform.OrganizationAsync(connection.OrganizationId, cancellationToken).ConfigureAwait(false) is null
            || await connections.DiscoverAsync(pending.Issuer, cancellationToken).ConfigureAwait(false) is not { } discovery)
        {
            return null;
        }

        string? idToken;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = this.RedirectUri,
                    ["code_verifier"] = pending.Verifier,
                }),
            };
            // RFC 6749 §2.3.1: the client id and secret are form-encoded before Basic encoding.
            request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{Uri.EscapeDataString(connection.ClientId!)}:{Uri.EscapeDataString(connections.ClientSecret(connection))}")));
            using var response = await clients.CreateClient(SsoConnections.HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            idToken = response.IsSuccessStatusCode && body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("id_token", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.ZLogWarning($"Audit: SSO token request for {connection.Domain:@Domain} failed ({error.GetType().Name})");
            return null;
        }

        var result = idToken is null ? null : await Handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = connection.Issuer,
            ValidAudience = connection.ClientId,
            ConfigurationManager = connections.Discovery(connection.Issuer!),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512, SecurityAlgorithms.RsaSsaPssSha256,
                SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512, SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512],
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            LifetimeValidator = (notBefore, expires, _, _) => expires > clock.GetUtcNow().UtcDateTime.AddMinutes(-1) && (notBefore is null || notBefore < clock.GetUtcNow().UtcDateTime.AddMinutes(1)),
        }).ConfigureAwait(false);
        var claims = result is { IsValid: true } ? result.Claims : null;
        string? Text(string name) => claims is not null && claims.TryGetValue(name, out var value) ? value as string : null;
        var verified = claims is null || !claims.TryGetValue("email_verified", out var flag) || flag is true || flag is string text && text == "true";
        // An identity provider that omits email_verified speaks for the domain its organization proved; an explicit false is refused.
        if (claims is null || Text("nonce") is not { } nonce || !Tokens.FixedTimeEquals(nonce, pending.Nonce)
            || Text("azp") is { } azp && azp != connection.ClientId || !verified
            || Users.NormalizeEmail(Text("email")) is not { } email || SsoConnections.EmailDomain(email) != connection.Domain)
        {
            logger.ZLogWarning($"Audit: SSO sign-in for {connection.Domain:@Domain} refused: the ID token failed validation");
            return null;
        }

        var user = await users.FindByEmailAsync(PlatformDirectory.Instance, email, cancellationToken).ConfigureAwait(false)
            ?? await users.CreateAsync(PlatformDirectory.Instance, email, cancellationToken).ConfigureAwait(false)
            ?? await users.FindByEmailAsync(PlatformDirectory.Instance, email, cancellationToken).ConfigureAwait(false);
        if (user is not null && await platform.RoleAsync(connection.OrganizationId, user.Id, cancellationToken).ConfigureAwait(false) is null
            && await platform.AddMemberAsync(connection.OrganizationId, user.Id, connection.DefaultRole, cancellationToken).ConfigureAwait(false))
        {
            logger.ZLogInformation($"Audit: SSO sign-in joined user {user.Id:@UserId} to organization {connection.OrganizationId:@OrganizationId} as {connection.DefaultRole:@Role}");
        }

        return user;
    }

    private string RedirectUri => new Uri(options.PublicOrigin, CallbackPath).AbsoluteUri;
}

[JsonSerializable(typeof(SsoState))]
internal sealed partial class SsoStateJson : JsonSerializerContext;
