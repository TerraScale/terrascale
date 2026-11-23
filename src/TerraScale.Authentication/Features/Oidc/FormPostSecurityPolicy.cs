using Microsoft.AspNetCore;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Oidc;

public sealed class FormPostSecurityPolicy : IOpenIddictServerHandler<ApplyAuthorizationResponseContext>
{
    public const string AutoSubmitScriptHash = "sha256-j7OoGArf6XW6YY4cAyS3riSSvrJRqpSi1fOF9vQ5SrI=";

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
