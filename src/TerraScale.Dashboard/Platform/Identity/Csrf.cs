using System.Security.Cryptography;
using System.Text;

namespace TerraScale.Dashboard.Platform.Identity;

public static class Csrf
{
    private const string Cookie = "ts-dashboard-csrf";

    public static string Token(HttpContext context)
    {
        var token = context.Request.Cookies[Cookie];
        if (token is not { Length: 64 } || token.Any(character => !char.IsAsciiHexDigitLower(character)))
        {
            token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            context.Response.Cookies.Append(Cookie, token, new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/"
            });
        }
        return token;
    }

    public static bool Validate(HttpContext context, IFormCollection form)
    {
        if (context.Request.Headers.Origin is { Count: > 0 } origins &&
            (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) ||
             origin.GetLeftPart(UriPartial.Authority) != $"{context.Request.Scheme}://{context.Request.Host}")) return false;
        if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site") return false;
        var cookie = context.Request.Cookies[Cookie];
        var token = form["_csrf"].ToString();
        return cookie is { Length: 64 } && token.Length == 64 &&
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(cookie), Encoding.ASCII.GetBytes(token));
    }

    public static string LocalRedirect(string? value) =>
        value is { Length: > 0 } && value.StartsWith('/') && !value.StartsWith("//") &&
        !value.Contains('\\') && !value.Any(char.IsControl) ? value : "/";
}
