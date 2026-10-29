using System.Security.Cryptography;
using System.Text;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Platform.Identity;

// Signed double-submit proof. The __Host- cookie holds a random nonce that a sibling subdomain
// cannot set; the form token is an HMAC of that nonce and the browser's session, keyed from the
// existing session encryption key. A form proof is valid only for the session it was issued to:
// anonymous forms (OAuth consent) bind to no session, and an account-deletion continuation binds
// to its recovery cookie, which keeps the original session handle after ts-auth revokes it.
public static class Csrf
{
    internal const string Cookie = "__Host-ts-dashboard-csrf";
    private const string NonceKey = "TerraScale.Dashboard/csrf-nonce";
    private const string BindingKey = "TerraScale.Dashboard/csrf-binding";

    public static string Token(HttpContext context)
    {
        if (context.Items[NonceKey] is not string nonce)
        {
            nonce = context.Request.Cookies[Cookie] ?? "";
            if (!Hex(nonce))
            {
                nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                context.Response.Cookies.Append(Cookie, nonce, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    Path = "/"
                });
            }
            context.Items[NonceKey] = nonce;
        }
        return Sign(context, nonce);
    }

    public static bool Validate(HttpContext context, IFormCollection form)
    {
        if (context.Request.Headers.Origin is { Count: > 0 } origins &&
            (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) ||
             origin.GetLeftPart(UriPartial.Authority) != $"{context.Request.Scheme}://{context.Request.Host}")) return false;
        if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site") return false;
        var nonce = context.Request.Cookies[Cookie];
        var token = form["_csrf"].ToString();
        return Hex(nonce) && Hex(token) &&
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(context, nonce!)), Encoding.ASCII.GetBytes(token));
    }

    // Called when this response changes the browser's session cookie, so forms rendered by the
    // same response bind to the session the browser will hold next ("" when it holds none).
    internal static void Bind(HttpContext context, string? session) => context.Items[BindingKey] = session ?? "";

    private static string Sign(HttpContext context, string nonce)
    {
        var session = context.Items[BindingKey] as string ?? context.Request.Cookies[DashboardSessions.Cookie];
        var deletion = context.Request.Cookies[AccountDeletion.Cookie];
        var binding = Hex(session) ? session! : Hex(deletion) ? deletion! : "";
        // The configured credentials are read synchronously from the environment or secret files.
        var credentials = context.RequestServices.GetRequiredService<IDashboardIdentity>().ReadAsync(context.RequestAborted).GetAwaiter().GetResult();
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Convert.FromBase64String(credentials.SessionEncryptionKey), 32, info: "ts-dashboard/csrf"u8.ToArray());
        try { return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(binding + "." + nonce))); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static bool Hex(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);

    public static string LocalRedirect(string? value) =>
        value is { Length: > 0 } && value.StartsWith('/') && !value.StartsWith("//") &&
        !value.Contains('\\') && !value.Any(char.IsControl) ? value : "/";
}
