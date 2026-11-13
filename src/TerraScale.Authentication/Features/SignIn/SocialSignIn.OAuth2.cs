using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.SignIn;

public sealed partial class SocialSignIn
{
    private static OAuth2Configuration? OAuth(SocialProviderRecord provider) => provider.Provider switch
    {
        SocialProviders.Discord => new("https://discord.com/oauth2/authorize", "https://discord.com/api/oauth2/token",
            "https://discord.com/api/users/@me", "identify email", EmailVerifiedClaim: "verified"),
        SocialProviders.Facebook => new("https://www.facebook.com/v24.0/dialog/oauth", "https://graph.facebook.com/v24.0/oauth/access_token",
            "https://graph.facebook.com/v24.0/me?fields=id,email", "email,public_profile"),
        SocialProviders.X => new("https://x.com/i/oauth2/authorize", "https://api.x.com/2/oauth2/token",
            "https://api.x.com/2/users/me?user.fields=confirmed_email", "users.read users.email tweet.read", "data.id", "data.confirmed_email"),
        SocialProviders.OAuth2 => provider.OAuth2 ?? throw new InvalidOperationException("A custom OAuth2 provider requires configuration."),
        _ => null,
    };

    private static string ValidIssuer(SocialProviderRecord provider, string configured, string issuer, SecurityToken token)
    {
        if (provider.Provider == SocialProviders.Microsoft && token is JsonWebToken jwt
            && jwt.TryGetPayloadValue<string>("tid", out var tenant) && Guid.TryParseExact(tenant, "D", out _)
            && issuer == $"https://login.microsoftonline.com/{tenant}/v2.0"
            && (configured == issuer || configured == "https://login.microsoftonline.com/{tenantid}/v2.0")
            && (provider.Tenant != "consumers" || tenant == "9188040d-6c67-4c5b-b112-36a304b66dad")
            && (provider.Tenant != "organizations" || tenant != "9188040d-6c67-4c5b-b112-36a304b66dad"))
        {
            return issuer;
        }

        if (provider.Provider != SocialProviders.Microsoft
            && (issuer == configured || provider.Provider == SocialProviders.Google && issuer == "accounts.google.com"))
        {
            return issuer;
        }

        throw new SecurityTokenInvalidIssuerException("The provider token names another issuer.");
    }

    private static async Task<SocialIdentity?> OAuthAsync(HttpClient client, SocialProviderRecord provider, OAuth2Configuration configuration,
        Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, configuration.TokenUrl);
        if (provider.Provider == SocialProviders.X)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(provider.ClientId) + ":" + Uri.EscapeDataString(form["client_secret"]))));
            form.Remove("client_secret");
        }

        request.Content = new FormUrlEncodedContent(form);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || await ReadAsync(response, cancellationToken).ConfigureAwait(false) is not { } body
            || Text(body, "access_token") is not { Length: > 0 } accessToken)
        {
            return null;
        }

        using var profile = new HttpRequestMessage(HttpMethod.Get, configuration.UserinfoUrl);
        profile.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var userinfo = await client.SendAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!userinfo.IsSuccessStatusCode || await ReadAsync(userinfo, cancellationToken).ConfigureAwait(false) is not { } user
            || Text(user, configuration.IdClaim) is not { Length: > 0 and <= 256 } subject)
        {
            return null;
        }

        var email = Text(user, configuration.EmailClaim) ?? string.Empty;
        var verified = provider.Provider is not (SocialProviders.Facebook or SocialProviders.X)
            && Claim(user, configuration.EmailVerifiedClaim) is { ValueKind: JsonValueKind.True };
        var authority = provider.Provider switch
        {
            SocialProviders.Discord => "https://discord.com",
            SocialProviders.Facebook => "https://www.facebook.com",
            SocialProviders.X => "https://x.com",
            _ => configuration.UserinfoUrl,
        };
        return new SocialIdentity(email, authority, subject, provider.ClientId, verified);
    }

    private static JsonElement? Claim(JsonElement value, string path)
    {
        foreach (var part in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value))
            {
                return null;
            }
        }

        return value;
    }

    private static string? Text(JsonElement value, string path) => Claim(value, path) is { } claim
        ? claim.ValueKind switch { JsonValueKind.String => claim.GetString(), JsonValueKind.Number => claim.GetRawText(), _ => null }
        : null;
}
