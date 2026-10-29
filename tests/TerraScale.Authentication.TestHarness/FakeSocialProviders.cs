using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Features.SignIn;

namespace TerraScale.Authentication.Tests;

/// <summary>
/// In-process stand-ins for Google, Apple, GitHub and a generic OIDC issuer, served through a message handler that
/// replaces ts-auth's <see cref="SocialSignIn.HttpClientName"/> transport (nothing leaves the process). Each token
/// endpoint checks the provider's contract: client credentials (Apple: an ES256 client-secret JWT from the project's
/// key), the registered redirect URI and the PKCE verifier, and answers what that provider answers.
/// </summary>
public sealed class FakeSocialProviders : IDisposable
{
    /// <summary>The generic OIDC issuer.</summary>
    public const string OidcIssuer = "https://idp.example.test/tenant";

    private static readonly JsonWebTokenHandler Tokens = new() { SetDefaultTimesOnTokenCreation = false };
    private readonly RSA rsa = RSA.Create(2048);
    private readonly RsaSecurityKey key;
    private readonly ConcurrentDictionary<string, Grant> accessTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Grant> grants = new(StringComparer.Ordinal);

    public FakeSocialProviders() => this.key = new RsaSecurityKey(this.rsa) { KeyId = Guid.NewGuid().ToString("N") };

    /// <summary>The project's client secret every token endpoint expects (Google, GitHub, OIDC).</summary>
    public string ClientSecret { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The signed Google issuer claim, independent of its fixed discovery issuer.</summary>
    public string GoogleTokenIssuer { get; set; } = "https://accounts.google.com";

    /// <summary>The project's Apple key; its public half verifies Apple client-secret JWTs.</summary>
    public ECDsa AppleKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Routes ts-auth's outbound social client to these providers.</summary>
    public void Route(IServiceCollection services) =>
        services.AddHttpClient(SocialSignIn.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Handler(this));

    /// <summary>
    /// The user approves the authorization request ts-auth redirected to: a one-time code for <paramref name="email"/>,
    /// bound to the request's client, redirect URI, nonce and PKCE challenge.
    /// </summary>
    public string Approve(Uri authorization, string email, bool verified = true, string? subject = "12345")
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var query = QueryHelpers.ParseQuery(authorization.Query);
        var code = Guid.NewGuid().ToString("N");
        string Value(string name) => query.TryGetValue(name, out var value) ? value.ToString() : string.Empty;
        this.grants[code] = new(authorization.Host, Value("client_id"), Value("redirect_uri"), Value("nonce"), Value("code_challenge"), email, verified, subject);
        return code;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.rsa.Dispose();
        this.AppleKey.Dispose();
    }

    private sealed record Grant(string Host, string ClientId, string RedirectUri, string Nonce, string Challenge, string Email, bool Verified, string? Subject);

    private string Jwks()
    {
        var parameters = this.rsa.ExportParameters(includePrivateParameters: false);
        return new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject
            {
                ["kty"] = "RSA",
                ["alg"] = "RS256",
                ["use"] = "sig",
                ["kid"] = this.key.KeyId,
                ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
                ["e"] = Base64UrlEncoder.Encode(parameters.Exponent),
            }),
        }.ToJsonString();
    }

    private static string Discovery(string issuer, string authorize, string token, string jwks) => new JsonObject
    {
        ["issuer"] = issuer,
        ["authorization_endpoint"] = authorize,
        ["token_endpoint"] = token,
        ["jwks_uri"] = jwks,
    }.ToJsonString();

    // The grant the form redeems, when its code, client, redirect URI and PKCE verifier match; single use.
    private Grant? Redeem(Dictionary<string, string> form, string host, bool pkce)
    {
        if (!form.TryGetValue("code", out var code) || !this.grants.TryRemove(code, out var grant) || grant.Host != host
            || form.GetValueOrDefault("grant_type") != "authorization_code" || form.GetValueOrDefault("client_id") != grant.ClientId
            || form.GetValueOrDefault("redirect_uri") != grant.RedirectUri)
        {
            return null;
        }

        if (pkce && (form.GetValueOrDefault("code_verifier") is not { } verifier || Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != grant.Challenge))
        {
            return null;
        }

        return grant;
    }

    private async Task<bool> AppleSecretAsync(string? secret, string clientId)
    {
        var result = await Tokens.ValidateTokenAsync(secret, new TokenValidationParameters
        {
            ValidIssuer = "ABCDE12345",
            ValidAudience = "https://appleid.apple.com",
            IssuerSigningKey = new ECDsaSecurityKey(this.AppleKey),
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        });
        return result.IsValid && result.Claims.TryGetValue("sub", out var subject) && (string)subject == clientId
            && new JsonWebToken(secret).Kid == "KEY1234567";
    }

    private string IdToken(string issuer, Grant grant, bool appleStyle)
    {
        var claims = new Dictionary<string, object>
        {
            ["iss"] = issuer,
            ["aud"] = grant.ClientId,
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            ["nonce"] = grant.Nonce,
            ["email"] = grant.Email,
            // Apple sends the flag as a string.
            ["email_verified"] = appleStyle ? (grant.Verified ? "true" : "false") : grant.Verified,
        };
        if (grant.Subject is not null)
        {
            claims["sub"] = grant.Subject;
        }

        return Tokens.CreateToken(JsonSerializer.Serialize(claims), new SigningCredentials(this.key, SecurityAlgorithms.RsaSha256));
    }

    private async Task<string?> AnswerAsync(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        var form = request.Content is null ? new Dictionary<string, string>()
            : QueryHelpers.ParseQuery(await request.Content.ReadAsStringAsync()).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
        switch (uri.Host, uri.AbsolutePath)
        {
            case ("accounts.google.com", "/.well-known/openid-configuration"):
                return Discovery("https://accounts.google.com", "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token", "https://www.googleapis.com/oauth2/v3/certs");
            case ("appleid.apple.com", "/.well-known/openid-configuration"):
                return Discovery("https://appleid.apple.com", "https://appleid.apple.com/auth/authorize", "https://appleid.apple.com/auth/token", "https://appleid.apple.com/auth/keys");
            case ("idp.example.test", "/tenant/.well-known/openid-configuration"):
                return Discovery(OidcIssuer, OidcIssuer + "/authorize", OidcIssuer + "/token", OidcIssuer + "/jwks");
            case ("www.googleapis.com", "/oauth2/v3/certs") or ("appleid.apple.com", "/auth/keys") or ("idp.example.test", "/tenant/jwks"):
                return this.Jwks();
            case ("oauth2.googleapis.com", "/token"):
                return form.GetValueOrDefault("client_secret") == this.ClientSecret && this.Redeem(form, "accounts.google.com", pkce: true) is { } google
                    ? new JsonObject { ["id_token"] = this.IdToken(this.GoogleTokenIssuer, google, false) }.ToJsonString() : null;
            case ("idp.example.test", "/tenant/token"):
                return form.GetValueOrDefault("client_secret") == this.ClientSecret && this.Redeem(form, "idp.example.test", pkce: true) is { } oidc
                    ? new JsonObject { ["id_token"] = this.IdToken(OidcIssuer, oidc, false) }.ToJsonString() : null;
            case ("appleid.apple.com", "/auth/token"):
                return form.TryGetValue("client_id", out var clientId) && await this.AppleSecretAsync(form.GetValueOrDefault("client_secret"), clientId)
                    && this.Redeem(form, "appleid.apple.com", pkce: false) is { } apple
                    ? new JsonObject { ["id_token"] = this.IdToken("https://appleid.apple.com", apple, true) }.ToJsonString() : null;
            case ("github.com", "/login/oauth/access_token"):
                // GitHub answers JSON only when asked to.
                if (!request.Headers.Accept.Any(accept => accept.MediaType == "application/json") || form.GetValueOrDefault("client_secret") != this.ClientSecret
                    || this.Redeem(form, "github.com", pkce: true) is not { } github)
                {
                    return null;
                }

                var token = "gho_" + Guid.NewGuid().ToString("N");
                this.accessTokens[token] = github;
                return new JsonObject { ["access_token"] = token, ["token_type"] = "bearer" }.ToJsonString();
            case ("api.github.com", "/user") or ("api.github.com", "/user/emails"):
                if (request.Headers.Authorization?.Parameter is not { } bearer || !this.accessTokens.TryGetValue(bearer, out var account)
                    || request.Headers.UserAgent.Count == 0)
                {
                    return null;
                }

                return uri.AbsolutePath == "/user"
                    ? new JsonObject { ["id"] = long.Parse(account.Subject!, System.Globalization.CultureInfo.InvariantCulture) }.ToJsonString()
                    : new JsonArray(
                        new JsonObject { ["email"] = "other-" + account.Email, ["primary"] = false, ["verified"] = true },
                        new JsonObject { ["email"] = account.Email, ["primary"] = true, ["verified"] = account.Verified }).ToJsonString();
            default:
                return null;
        }
    }

    private sealed class Handler(FakeSocialProviders providers) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            await providers.AnswerAsync(request) is { } body
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json") };
    }
}
