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
public sealed record SocialProviderRecord(string Provider, string ClientId, string SecretBox, string? Issuer = null, string? TeamId = null, string? KeyId = null);

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

    /// <summary>Every provider, in the order pages and the API list them.</summary>
    public static IReadOnlyList<string> Names { get; } = [Google, GitHub, Apple, Oidc];

    /// <summary>The label the hosted button shows.</summary>
    public static string Label(string provider) => provider switch
    {
        Google => "Google",
        GitHub => "GitHub",
        Apple => "Apple",
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
        string? clientId, string? secret, string? issuer, string? teamId, string? keyId)
    {
        ArgumentNullException.ThrowIfNull(options);
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
        return new(provider, clientId, box, issuer?.TrimEnd('/'), teamId, keyId);
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
