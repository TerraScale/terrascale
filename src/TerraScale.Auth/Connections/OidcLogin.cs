namespace TerraScale.Auth.Connections;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

internal static class OidcLogin
{
    // ---- Upstream metadata and keys -------------------------------------------------------

    internal sealed record OidcDiscovery(string? Issuer, string? AuthorizationEndpoint, string? TokenEndpoint, string? UserinfoEndpoint, string? JwksUri);

    /// <summary>Fetches {issuer}/.well-known/openid-configuration; null on any transport or shape failure.</summary>
    internal static async Task<OidcDiscovery?> FetchDiscoveryAsync(Uri issuer, CancellationToken ct)
    {
        try
        {
            using var response = await ConnectionFlow.Upstream.GetAsync(issuer.ToString().TrimEnd('/') + "/.well-known/openid-configuration", ct);
            if (!response.IsSuccessStatusCode)
                return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return new OidcDiscovery(
                StringProperty(document.RootElement, "issuer"),
                StringProperty(document.RootElement, "authorization_endpoint"),
                StringProperty(document.RootElement, "token_endpoint"),
                StringProperty(document.RootElement, "userinfo_endpoint"),
                StringProperty(document.RootElement, "jwks_uri"));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    internal static string CallbackUrl(AuthOptions options, string slug, string connectionId) =>
        $"{options.PublicOrigin.ToString().TrimEnd('/')}/{slug}/connections/{connectionId}/oidc/callback";

    internal static string Query(Dictionary<string, string> parameters) =>
        string.Join("&", parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    internal static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
