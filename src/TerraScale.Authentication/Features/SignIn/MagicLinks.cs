using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Features.Ui;

namespace TerraScale.Authentication.Features.SignIn;

internal sealed record MagicLinkProof(string Email, string Code, string BrowserHash, string? Next, long ExpiresAt, string IssueId)
{
    public override string ToString() => nameof(MagicLinkProof);
}

internal static class MagicLinks
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    internal static string Cookie(string tenant) => "tsauth_magic_" + tenant;

    internal static CookieOptions CookieOptions(string tenant) => new()
    {
        Path = $"/ui/{tenant}/sign-in",
        Secure = true,
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        MaxAge = Lifetime,
    };

    internal static IResult CodePage(HttpContext context, MagicLinkProof proof, string token, string error, int status = StatusCodes.Status200OK) =>
        SignInPageModel.Render(context, new SignInPageModel(SignInStep.Code, SignInFlow.Tenant(context), Mask(proof.Email),
            SignInFlow.SafeNext(context, proof.Next), Error: error)
        { Challenge = token, CodeRequest = "sign-in/magic" }, status);

    internal static string Mask(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        var dot = email.LastIndexOf('.');
        return email[..1] + "***@" + email[(at + 1)..(at + 2)] + "***" + email[dot..];
    }

    internal static Uri Create(AuthOptions options, IssuedPasscode issued, string browserHash, string? next)
    {
        var proof = new MagicLinkProof(issued.Email, issued.Code, browserHash, next, issued.ExpiresAt.ToUnixTimeSeconds(), issued.IssueId);
        var token = SecretBox.Seal(options.MasterKey, JsonSerializer.SerializeToUtf8Bytes(proof, MagicLinkJson.Default.MagicLinkProof), Context(issued.Tenant));
        return new Uri(options.PublicOrigin, $"ui/{issued.Tenant}/sign-in/magic?token={Uri.EscapeDataString(token)}");
    }

    internal static MagicLinkProof? Open(AuthOptions options, string tenant, string? token, TimeProvider clock)
    {
        if (token is not { Length: > 0 and <= 8192 })
        {
            return null;
        }

        try
        {
            var proof = JsonSerializer.Deserialize(SecretBox.Open(options.MasterKey, token, Context(tenant)), MagicLinkJson.Default.MagicLinkProof);
            return proof?.ExpiresAt > clock.GetUtcNow().ToUnixTimeSeconds() ? proof : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    private static string Context(string tenant) => "magic-link:" + tenant;
}

[JsonSerializable(typeof(MagicLinkProof))]
internal sealed partial class MagicLinkJson : JsonSerializerContext;
