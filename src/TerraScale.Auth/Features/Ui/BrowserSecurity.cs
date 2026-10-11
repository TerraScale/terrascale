using TerraScale.Auth.Hosting;
using TerraScale.Auth.Security;

namespace TerraScale.Auth.Features.Ui;

/// <summary>Response headers and cookies shared by every hosted page.</summary>
public static class BrowserSecurity
{
    /// <summary>Creates a fresh CSP nonce for one response.</summary>
    public static string NewNonce() => Tokens.RandomToken(24);

    /// <summary>
    /// Applies the hosted-page CSP with <paramref name="nonce"/> allowing only this response's
    /// inline styles and scripts, plus no-store, nosniff, frame and referrer protection.
    /// </summary>
    public static void ApplyPageHeaders(HttpResponse response, string nonce)
    {
        ArgumentNullException.ThrowIfNull(response);
        var headers = response.Headers;
        headers.CacheControl = "no-store";
        headers.ContentSecurityPolicy =
            $"default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'nonce-{nonce}'; img-src 'self' data: https:; "
            + "font-src 'self'; connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'; object-src 'none'";
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    }

    /// <summary>Sets the hosted session cookie: Secure, HttpOnly, SameSite=Lax, whole-origin path.</summary>
    public static void SetSessionCookie(HttpResponse response, string value)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Append(SecuritySchemes.SessionCookieName, value, new CookieOptions
        {
            Path = "/",
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
        });
    }

    /// <summary>Expires the hosted session cookie with the same path and flags.</summary>
    public static void DeleteSessionCookie(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Delete(SecuritySchemes.SessionCookieName, new CookieOptions
        {
            Path = "/",
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
        });
    }
}
