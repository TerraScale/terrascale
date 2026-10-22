using Scalar.AspNetCore;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Security;

namespace TerraScale.Authentication.Features.Ui;

/// <summary>Response headers and cookies shared by every hosted page.</summary>
public static class BrowserSecurity
{
    /// <summary>Applies baseline protection after endpoint and error handling, retaining page-specific CSPs.</summary>
    public static void ApplyResponseHeaders(HttpResponse response, bool https)
    {
        ArgumentNullException.ThrowIfNull(response);
        var headers = response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        if (https)
        {
            headers.StrictTransportSecurity = "max-age=31536000";
        }

        if (headers.ContentSecurityPolicy.Count == 0)
        {
            // Scalar generates its nonce while rendering; OnStarting runs after it and after error responses clear headers.
            // Scalar 2.17.13 also injects these fixed styles without nonces; hashes keep other inline style elements blocked.
            headers.ContentSecurityPolicy = response.HttpContext.Items[ScalarOptions.NonceHttpContextItemKey] is string nonce
                ? $"default-src 'none'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'nonce-{nonce}' "
                    + "'sha256-vdYLq+JIuYUT8YppztWIsjJdCfzEYhgcJp8cEVP6zzY=' "
                    + "'sha256-DLlkYCuDYGe6ME23YkUFB7Tc36rGDeo4mVBFbZX3uaU=' "
                    + "'sha256-nT0S1Qe6I3DXxjh7v5KM5jmdlxs04kQiH5PEGYxqKcU=' "
                    + "'sha256-47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=' "
                    + "'sha256-ZW1GSuo0pKyEx5Ar7ZC5Dflb/mjatjeTW9aVx0iZAzE='; "
                    + "style-src-attr 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; form-action 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'"
                : "default-src 'none'; form-action 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'";
        }
    }

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
