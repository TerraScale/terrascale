using Microsoft.AspNetCore;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>
/// Gives OpenIddict's response_mode=form_post page its own CSP: only OpenIddict's fixed autosubmit script may run, and the
/// form may post only to the callback's origin. It runs after <see cref="ProjectRedirectPolicy"/> and OpenIddict's own
/// validation, so the redirect URI is already a registered (and project-allowed) callback; every other response keeps the
/// generic CSP from <see cref="Ui.BrowserSecurity"/>.
/// </summary>
public sealed class FormPostSecurityPolicy : IOpenIddictServerHandler<ApplyAuthorizationResponseContext>
{
    /// <summary>SHA-256 of OpenIddict 7.7.1's inline <c>document.form.submit();</c>, the only script on the page.</summary>
    public const string AutoSubmitScriptHash = "sha256-j7OoGArf6XW6YY4cAyS3riSSvrJRqpSi1fOF9vQ5SrI=";

    /// <inheritdoc />
    public ValueTask HandleAsync(ApplyAuthorizationResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ResponseMode == ResponseModes.FormPost
            && FormTarget(context.RedirectUri) is { } target
            && context.Transaction.GetHttpRequest()?.HttpContext is { } http)
        {
            var headers = http.Response.Headers;
            headers.CacheControl = "no-store";
            headers.ContentSecurityPolicy = $"default-src 'none'; script-src '{AutoSubmitScriptHash}'; form-action {target}; "
                + "base-uri 'none'; object-src 'none'; frame-ancestors 'none'";
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The CSP source allowing a post to <paramref name="redirectUri"/>: its origin for http(s) DNS and IPv4 hosts (IDN as
    /// punycode), or its scheme alone where CSP has no host syntax (IPv6 literals, native-app schemes).
    /// </summary>
    public static string? FormTarget(string? redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) || !IsCspToken(uri.Scheme))
        {
            return null;
        }

        if (uri.Scheme is not ("http" or "https") || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4))
        {
            return uri.Scheme + ":";
        }

        var origin = $"{uri.Scheme}://{uri.IdnHost}" + (uri.IsDefaultPort ? string.Empty : $":{uri.Port}");
        return IsCspToken(origin) ? origin : null;
    }

    private static bool IsCspToken(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or ':' or '/');
}
