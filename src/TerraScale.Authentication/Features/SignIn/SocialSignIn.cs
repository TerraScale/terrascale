using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Security;

namespace TerraScale.Authentication.Features.SignIn;

/// <summary>A validated external identity and its verified contact address; neither replaces the local user ID.</summary>
/// <param name="Email">The verified address.</param>
/// <param name="Issuer">The validated authority: Google aliases are canonicalized; other OIDC issuers stay exact.</param>
/// <param name="Subject">The stable external subject.</param>
/// <param name="ClientId">The client whose token was validated.</param>
public sealed record SocialIdentity(string Email, string Issuer, string Subject, string ClientId);

/// <summary>A sign-in in flight, sealed into the browser's state cookie.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="State">The <c>state</c> sent to the provider.</param>
/// <param name="Nonce">The OIDC <c>nonce</c>.</param>
/// <param name="Verifier">The PKCE verifier.</param>
/// <param name="Next">The validated page to resume.</param>
/// <param name="ExpiresAt">Unix seconds after which the callback is refused.</param>
/// <param name="ConfigurationHash">The provider configuration and sealed credential generation at start.</param>
public sealed record SocialState(string Provider, string State, string Nonce, string Verifier, string? Next, long ExpiresAt, string ConfigurationHash);

/// <summary>
/// A project's end users sign in with the project's own OAuth client at Google, GitHub, Apple or an OpenID Connect
/// provider (project-social-sign-in.md): Authorization Code with <c>state</c>, PKCE where the provider supports it and
/// an OIDC <c>nonce</c>, all bound to the browser by an encrypted, short-lived cookie. Google, Apple and generic OIDC
/// prove the address with a validated ID token; GitHub with its verified primary email.
/// </summary>
public sealed class SocialSignIn(IHttpClientFactory clients, AuthOptions options, TimeProvider clock)
{
    /// <summary>The outbound client; it connects only to public addresses (<c>PublicAddresses</c>).</summary>
    public const string HttpClientName = "social-sign-in";

    /// <summary>How long a started sign-in may take.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private const string GoogleIssuer = "https://accounts.google.com";
    private const string AppleIssuer = "https://appleid.apple.com";
    private const string GitHubAuthorize = "https://github.com/login/oauth/authorize";
    private const string GitHubToken = "https://github.com/login/oauth/access_token";
    private const string GitHubEmails = "https://api.github.com/user/emails";
    private const int MaxIssuers = 1024;

    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> configurations = new(StringComparer.Ordinal);
    private readonly JsonWebTokenHandler tokens = new();

    /// <summary>The state cookie of <paramref name="tenant"/>'s hosted pages.</summary>
    public static string Cookie(string tenant) => "tsauth_social_" + tenant;

    /// <summary>Starts a sign-in: the provider's authorization URL and the sealed state for the cookie.</summary>
    public async Task<(Uri Location, string Sealed)> StartAsync(string tenant, SocialProviderRecord provider, string? next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var state = new SocialState(provider.Provider, Tokens.RandomToken(32), Tokens.RandomToken(32), Tokens.RandomToken(43), next, (clock.GetUtcNow() + Lifetime).ToUnixTimeSeconds(), ConfigurationHash(provider));
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = provider.ClientId,
            ["redirect_uri"] = SocialProviders.Callback(options, tenant, provider.Provider).AbsoluteUri,
            ["state"] = state.State,
        };
        string endpoint;
        if (provider.Provider == SocialProviders.GitHub)
        {
            endpoint = GitHubAuthorize;
            query["scope"] = "read:user user:email";
            query["allow_signup"] = "true";
        }
        else
        {
            endpoint = (await this.ConfigurationAsync(provider, cancellationToken).ConfigureAwait(false)).AuthorizationEndpoint;
            query["scope"] = "openid email";
            query["nonce"] = state.Nonce;
            if (provider.Provider == SocialProviders.Apple)
            {
                // Apple posts the code back when email is requested.
                query["response_mode"] = "form_post";
            }
        }

        if (provider.Provider != SocialProviders.Apple)
        {
            query["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(state.Verifier)));
            query["code_challenge_method"] = "S256";
        }

        var location = new Uri(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(endpoint, query!));
        var json = JsonSerializer.SerializeToUtf8Bytes(state, SocialJson.Default.SocialState);
        return (location, SecretBox.Seal(options.MasterKey, json, StateContext(tenant)));
    }

    /// <summary>The cookie's sign-in when it is this provider's, unexpired and carries <paramref name="state"/>; otherwise null.</summary>
    public SocialState? Resume(string tenant, string provider, string? cookie, string? state)
    {
        if (cookie is null || state is null)
        {
            return null;
        }

        try
        {
            var opened = JsonSerializer.Deserialize(SecretBox.Open(options.MasterKey, cookie, StateContext(tenant)), SocialJson.Default.SocialState);
            return opened is not null && opened.Provider == provider && opened.ExpiresAt > clock.GetUtcNow().ToUnixTimeSeconds()
                && Tokens.FixedTimeEquals(opened.State, state) ? opened : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Redeems the code and returns the validated identity, or null when its proof or configuration changed.</summary>
    public async Task<SocialIdentity?> CompleteAsync(string tenant, SocialProviderRecord provider, SocialState state, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(state);
        if (state.Provider != provider.Provider || state.ConfigurationHash != ConfigurationHash(provider))
        {
            return null;
        }

        var secret = provider.Provider == SocialProviders.Apple ? this.AppleSecret(tenant, provider) : SocialProviders.Open(options, tenant, provider);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = SocialProviders.Callback(options, tenant, provider.Provider).AbsoluteUri,
            ["client_id"] = provider.ClientId,
            ["client_secret"] = secret,
        };
        if (provider.Provider != SocialProviders.Apple)
        {
            form["code_verifier"] = state.Verifier;
        }

        var client = clients.CreateClient(HttpClientName);
        if (provider.Provider == SocialProviders.GitHub)
        {
            return await GitHubAsync(client, form, cancellationToken).ConfigureAwait(false);
        }

        var configuration = await this.ConfigurationAsync(provider, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, configuration.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || await ReadAsync(response, cancellationToken).ConfigureAwait(false) is not { } body
            || !body.TryGetProperty("id_token", out var idToken) || idToken.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var validated = await this.tokens.ValidateTokenAsync(idToken.GetString(), new TokenValidationParameters
        {
            ValidIssuers = provider.Provider == SocialProviders.Google ? [GoogleIssuer, "accounts.google.com"] : [configuration.Issuer],
            ValidAudience = provider.ClientId,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256],
            RequireExpirationTime = true,
            RequireSignedTokens = true,
        }).ConfigureAwait(false);
        object? Claim(string name) => validated.Claims.TryGetValue(name, out var value) ? value : null;
        if (!validated.IsValid || Claim("nonce") is not string nonce || !Tokens.FixedTimeEquals(nonce, state.Nonce)
            || Claim("iss") is not string issuer || Claim("sub") is not string subject
            || string.IsNullOrWhiteSpace(subject) || subject.Length > 256
            || Claim("email") is not string email || Claim("email_verified") is not (true or "true"))
        {
            return null;
        }

        // Both accepted Google spellings name one authority; map only after token validation.
        return new(email, provider.Provider == SocialProviders.Google ? GoogleIssuer : issuer, subject, provider.ClientId);
    }

    // GitHub: stable account ID and verified primary email from the same access token.
    private static async Task<SocialIdentity?> GitHubAsync(HttpClient client, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GitHubToken) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || await ReadAsync(response, cancellationToken).ConfigureAwait(false) is not { } body
            || !body.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        using var profile = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        profile.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.GetString());
        profile.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        profile.Headers.UserAgent.Add(new ProductInfoHeaderValue("ts-auth", "1"));
        using var account = await client.SendAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccessStatusCode || await ReadAsync(account, cancellationToken).ConfigureAwait(false) is not { } user
            || !user.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) || number <= 0)
        {
            return null;
        }

        using var emails = new HttpRequestMessage(HttpMethod.Get, GitHubEmails);
        emails.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.GetString());
        emails.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        emails.Headers.UserAgent.Add(new ProductInfoHeaderValue("ts-auth", "1"));
        using var listed = await client.SendAsync(emails, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccessStatusCode || await ReadAsync(listed, cancellationToken).ConfigureAwait(false) is not { ValueKind: JsonValueKind.Array } addresses)
        {
            return null;
        }

        return addresses.EnumerateArray()
            .Where(address => address.ValueKind == JsonValueKind.Object && address.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True
                && address.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True
                && address.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String)
            .Select(address => new SocialIdentity(address.GetProperty("email").GetString()!, "https://github.com", number.ToString(CultureInfo.InvariantCulture), form["client_id"]))
            .FirstOrDefault();
    }

    // Discovery of Google, Apple or the configured issuer; the document must name the same issuer (OIDC Discovery 4.3).
    private async Task<OpenIdConnectConfiguration> ConfigurationAsync(SocialProviderRecord provider, CancellationToken cancellationToken)
    {
        var issuer = provider.Provider switch
        {
            SocialProviders.Google => GoogleIssuer,
            SocialProviders.Apple => AppleIssuer,
            _ => provider.Issuer ?? throw new InvalidOperationException("An oidc provider has an issuer."),
        };
        if (this.configurations.Count >= MaxIssuers)
        {
            this.configurations.Clear();
        }

        var manager = this.configurations.GetOrAdd(issuer, key => new ConfigurationManager<OpenIdConnectConfiguration>(
            key + "/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(clients.CreateClient(HttpClientName)) { RequireHttps = true }));
        var configuration = await manager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        return configuration.Issuer == issuer ? configuration : throw new InvalidOperationException("The provider's discovery document names another issuer.");
    }

    // Apple's client secret: a short ES256 JWT signed with the project's .p8 key.
    private string AppleSecret(string tenant, SocialProviderRecord provider)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(SocialProviders.Open(options, tenant, provider));
        var now = clock.GetUtcNow().UtcDateTime;
        return this.tokens.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = provider.TeamId,
            Audience = AppleIssuer,
            Subject = new ClaimsIdentity([new Claim("sub", provider.ClientId)]),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            // The key is disposed after signing, so its signature provider must not be cached.
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(key) { KeyId = provider.KeyId }, SecurityAlgorithms.EcdsaSha256)
            {
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
            },
        });
    }

    private static async Task<JsonElement?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ConfigurationHash(SocialProviderRecord provider) =>
        Tokens.Sha256Hex(JsonSerializer.Serialize(provider, SocialJson.Default.SocialProviderRecord));

    private static string StateContext(string tenant) => "social-state:" + tenant;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SocialState))]
[JsonSerializable(typeof(SocialProviderRecord))]
[JsonSerializable(typeof(SocialIdentity))]
internal sealed partial class SocialJson : JsonSerializerContext;
