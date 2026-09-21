namespace TerraScale.Auth.OAuth;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.OAuth.OAuthSupport;

internal sealed class GetUserInfoEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/{slug}/oauth/userinfo", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, KeyRing keys,
            AuthOptions options, CancellationToken ct)
    {
        if (await store.FindInstanceBySlugAsync(slug, ct) is not { } instance)
            return ApiResults.NotFound();

        if (BearerToken(request) is not { } token)
            return UnauthorizedBearer(request, "missing bearer token");

        var claims = await VerifyAccessTokenAsync(keys, store, options, instance, token, ct);
        if (claims is null || ClaimString(claims, "sub") is not { } sub || await store.GetUserAsync(sub, ct) is not { } user)
            return UnauthorizedBearer(request, "invalid access token");

        var granted = (ClaimString(claims, "scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var body = new Dictionary<string, object> { ["sub"] = user.Id };
        if (granted.Contains("email") && user.Email is not null)
        {
            body["email"] = user.Email;
            body["email_verified"] = user.EmailVerified;
        }
        if (granted.Contains("profile") && user.Name is not null)
            body["name"] = user.Name;
        return Results.Json(body);
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    /// <summary>RFC 6750 3: every 401 from a bearer resource carries WWW-Authenticate; the error code is omitted when no token was presented.</summary>
    private static IResult UnauthorizedBearer(HttpRequest request, string description)
    {
        var missing = description == "missing bearer token";
        request.HttpContext.Response.Headers.WWWAuthenticate = missing
            ? "Bearer"
            : $"Bearer error=\"invalid_token\", error_description=\"{description}\"";
        return Results.Json(new { error = missing ? "invalid_request" : "invalid_token", error_description = description }, statusCode: 401);
    }
}
