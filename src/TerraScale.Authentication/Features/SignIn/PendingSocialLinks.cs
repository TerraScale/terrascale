using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Security;

namespace TerraScale.Authentication.Features.SignIn;

internal sealed record PendingSocialLink(string Id, string Email, LinkedProvider Identity, SocialProviderRecord Provider, long ExpiresAt)
{
    public override string ToString() => nameof(PendingSocialLink);
}

internal static class PendingSocialLinks
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    internal static string Cookie(string tenant) => "tsauth_social_link_" + tenant;

    internal static CookieOptions CookieOptions(string tenant) => new()
    {
        Path = $"/ui/{tenant}",
        Secure = true,
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        MaxAge = Lifetime,
    };

    internal static string Seal(AuthOptions options, string tenant, PendingSocialLink proof) =>
        SecretBox.Seal(options.MasterKey, JsonSerializer.SerializeToUtf8Bytes(proof, PendingSocialJson.Default.PendingSocialLink), Context(tenant));

    internal static PendingSocialLink? Open(AuthOptions options, string tenant, string? token, TimeProvider clock)
    {
        if (token is not { Length: > 0 and <= 16384 })
        {
            return null;
        }

        try
        {
            var proof = JsonSerializer.Deserialize(SecretBox.Open(options.MasterKey, token, Context(tenant)), PendingSocialJson.Default.PendingSocialLink);
            return proof?.ExpiresAt > clock.GetUtcNow().ToUnixTimeSeconds() ? proof : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    private static string Context(string tenant) => "pending-social-link:" + tenant;
}

[JsonSerializable(typeof(PendingSocialLink))]
internal sealed partial class PendingSocialJson : JsonSerializerContext;
