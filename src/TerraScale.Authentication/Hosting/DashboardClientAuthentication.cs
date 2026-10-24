using System.Security.Cryptography;
using System.Text;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;

namespace TerraScale.Authentication.Hosting;

/// <summary>Authentication shared by private APIs for the configured ts-dashboard confidential client.</summary>
internal static class DashboardClientAuthentication
{
    public static IResult? Refuse(HttpContext context, AuthOptions options)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Authenticated(context.Request.Headers.Authorization, options))
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"ts-auth\", charset=\"UTF-8\"";
            return Failure(StatusCodes.Status401Unauthorized, "invalid_client", "Authenticate as the ts-dashboard client with HTTP Basic.");
        }

        return string.Equals(context.GetRouteValue("slug") as string, OidcIssuer.Slug, StringComparison.Ordinal)
            ? null
            : Failure(StatusCodes.Status404NotFound, "not_found", "No such instance.");
    }

    private static bool Authenticated(string? authorization, AuthOptions options)
    {
        if (options.DashboardClient is not { } dashboard || authorization is null
            || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string credentials;
        try
        {
            credentials = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(authorization["Basic ".Length..].Trim()));
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException)
        {
            return false;
        }

        var separator = credentials.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            return false;
        }

        // Both parts are form-urlencoded before encoding (RFC 6749 section 2.3.1). Hashing first keeps the comparison
        // constant-time whatever the lengths.
        var id = System.Net.WebUtility.UrlDecode(credentials[..separator]);
        var secret = System.Net.WebUtility.UrlDecode(credentials[(separator + 1)..]);
        var match = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), SHA256.HashData(Encoding.UTF8.GetBytes(dashboard.Secret)));
        return match && string.Equals(id, OidcIssuer.DashboardClientId, StringComparison.Ordinal);
    }

    private static Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ErrorBody> Failure(int status, string code, string message) =>
        TypedResults.Json(new ErrorBody(new ErrorDetail(code, message)), statusCode: status);
}
