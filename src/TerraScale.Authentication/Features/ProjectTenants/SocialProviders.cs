using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Security;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary>
/// A sign-in provider a project configured with its own OAuth client (project-social-sign-in.md). The secret, or
/// Apple's .p8 private key, is sealed under the master key and bound to the project and provider.
/// </summary>
/// <param name="Provider">One of <see cref="SocialProviders.Names"/>.</param>
/// <param name="ClientId">The project's client id at the provider (Apple: its Services ID).</param>
/// <param name="SecretBox">The sealed client secret, or Apple's sealed private key PEM.</param>
/// <param name="Issuer">The generic OIDC provider's https issuer; null otherwise.</param>
/// <param name="TeamId">Apple's team id; null otherwise.</param>
/// <param name="KeyId">Apple's key id; null otherwise.</param>
public sealed record SocialProviderRecord(string Provider, string ClientId, string SecretBox, string? Issuer = null, string? TeamId = null, string? KeyId = null, string? Tenant = null, OAuth2Configuration? OAuth2 = null);

public sealed record OAuth2Configuration(string AuthorizeUrl, string TokenUrl, string UserinfoUrl, string Scopes, string IdClaim = "id", string EmailClaim = "email", string EmailVerifiedClaim = "email_verified");

/// <summary>The supported providers and the checks and sealing of their configuration.</summary>
public static partial class SocialProviders
{
    /// <summary>Google.</summary>
    public const string Google = "google";

    /// <summary>GitHub (OAuth apps).</summary>
    public const string GitHub = "github";

    /// <summary>Sign in with Apple.</summary>
    public const string Apple = "apple";

    /// <summary>Any OpenID Connect provider, by its issuer.</summary>
    public const string Oidc = "oidc";

    public const string Microsoft = "microsoft";
    public const string GitLab = "gitlab";
    public const string LinkedIn = "linkedin";
    public const string Slack = "slack";
    public const string Discord = "discord";
    public const string Facebook = "facebook";
    public const string X = "x";
    public const string OAuth2 = "oauth2";

    public static IReadOnlyList<string> Names { get; } = [Google, GitHub, Apple, Microsoft, GitLab, LinkedIn, Slack, Discord, Facebook, X, Oidc, OAuth2];

    /// <summary>The label the hosted button shows.</summary>
    public static string Label(string provider) => provider switch
    {
        Google => "Google",
        GitHub => "GitHub",
        Apple => "Apple",
        Microsoft => "Microsoft",
        GitLab => "GitLab",
        LinkedIn => "LinkedIn",
        Slack => "Slack",
        Discord => "Discord",
        Facebook => "Facebook",
        X => "X",
        OAuth2 => "Custom OAuth2",
        _ => "single sign-on",
    };

    /// <summary>The callback the customer registers at the provider.</summary>
    public static Uri Callback(AuthOptions options, string projectId, string provider)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(options.PublicOrigin, $"ui/{projectId}/social/{provider}/callback");
    }

    /// <summary>
    /// Validates a configuration and seals <paramref name="secret"/>; without a secret the stored one is kept, which
    /// only an existing configuration of the same provider has. Throws 400 <c>invalid_request</c>.
    /// </summary>
    public static SocialProviderRecord Seal(AuthOptions options, string projectId, string provider, SocialProviderRecord? current,
        string? clientId, string? secret, string? issuer, string? teamId, string? keyId, string? tenant = null, OAuth2Configuration? oauth2 = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Names.Contains(provider))
        {
            throw Invalid("Unknown social provider.");
        }

        if (provider == Microsoft ? tenant is not (null or "common" or "organizations" or "consumers") : tenant is not null)
        {
            throw Invalid("tenant is common, organizations or consumers, and only for microsoft.");
        }

        if (provider == OAuth2 ? !ValidOAuth2(oauth2) : oauth2 is not null)
        {
            throw Invalid("oauth2 requires HTTPS authorize, token and userinfo URLs, scopes and id, email and email_verified claim names, and only for oauth2.");
        }

        if (clientId is not { Length: > 0 and <= 256 } || clientId.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw Invalid("client_id must be 1 to 256 characters without spaces.");
        }

        if (secret is null && current is null)
        {
            throw Invalid("client_secret is required the first time a provider is configured.");
        }

        if (secret is { Length: 0 or > 8192 })
        {
            throw Invalid("client_secret must be 1 to 8192 characters.");
        }

        if (provider == Oidc ? !Issuer(issuer) : issuer is not null)
        {
            throw Invalid("issuer is an https URL without query or fragment, and only for oidc.");
        }

        if (provider == Apple ? !AppleId().IsMatch(teamId ?? string.Empty) || !AppleId().IsMatch(keyId ?? string.Empty) : teamId is not null || keyId is not null)
        {
            throw Invalid("team_id and key_id are Apple's 10-character ids, and only for apple.");
        }

        if (provider == Apple && secret is not null && !AppleKey(secret))
        {
            throw Invalid("client_secret for apple is the .p8 private key file (an EC P-256 PEM key).");
        }

        var box = secret is null ? current!.SecretBox : SecretBox.Seal(options.MasterKey, Encoding.UTF8.GetBytes(secret), Context(projectId, provider));
        return new(provider, clientId, box, issuer?.TrimEnd('/'), teamId, keyId, provider == Microsoft ? tenant ?? "common" : null, oauth2);
    }

    /// <summary>The stored secret in clear, for the token request only; never logged or answered.</summary>
    public static string Open(AuthOptions options, string projectId, SocialProviderRecord record)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(record);
        return Encoding.UTF8.GetString(SecretBox.Open(options.MasterKey, record.SecretBox, Context(projectId, record.Provider)));
    }

    private static string Context(string projectId, string provider) => $"social-provider:{projectId}:{provider}";

    private static bool Issuer(string? issuer) => issuer is { Length: <= 512 } && Uri.TryCreate(issuer, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static bool Endpoint(string? value) => value is { Length: <= 2048 } && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);

    private static bool ValidOAuth2(OAuth2Configuration? configuration) => configuration is not null
        && Endpoint(configuration.AuthorizeUrl) && Endpoint(configuration.TokenUrl) && Endpoint(configuration.UserinfoUrl)
        && !string.IsNullOrWhiteSpace(configuration.Scopes) && configuration.Scopes is { Length: <= 512 } && !configuration.Scopes.Any(char.IsControl)
        && new[] { configuration.IdClaim, configuration.EmailClaim, configuration.EmailVerifiedClaim }
            .All(claim => claim is { Length: > 0 and <= 128 } && claim.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-'));

    private static bool AppleKey(string pem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            return key.KeySize == 256 && key.ExportParameters(includePrivateParameters: true).D is { Length: > 0 };
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    private static ApiException Invalid(string message) => new(StatusCodes.Status400BadRequest, "invalid_request", message);

    [GeneratedRegex("^[A-Z0-9]{10}$")]
    private static partial Regex AppleId();
}
