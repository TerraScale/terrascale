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

internal sealed class IntrospectTokenEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/{slug}/oauth/introspect", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, KeyRing keys,
            AuthOptions options, CancellationToken ct)
    {
        if (await store.FindInstanceBySlugAsync(slug, ct) is not { } instance)
            return ApiResults.NotFound();
        var form = await request.ReadFormAsync(ct);
        var authentication = await AuthenticateClientAsync(request, form, store, instance.Id, ct);
        if (authentication.Client is null)
            return TokenError("invalid_client", authentication.Description ?? "client authentication failed", 401);

        var tokenValue = form["token"].ToString();
        if (tokenValue.Length == 0)
            return TokenError("invalid_request", "token is required");
        if (tokenValue.Contains('.'))
        {
            var claims = await VerifyAccessTokenAsync(keys, store, options, instance, tokenValue, ct);
            if (claims is not null)
                return Results.Json(new Dictionary<string, object>
                {
                    ["active"] = true,
                    ["token_type"] = "Bearer",
                    ["iss"] = ClaimString(claims, "iss") ?? "",
                    ["sub"] = ClaimString(claims, "sub") ?? "",
                    ["aud"] = ClaimString(claims, "aud") ?? "",
                    ["client_id"] = ClaimString(claims, "aud") ?? "",
                    ["scope"] = ClaimString(claims, "scope") ?? "",
                    ["exp"] = ClaimLong(claims, "exp") ?? 0,
                    ["iat"] = ClaimLong(claims, "iat") ?? 0,
                });
        }
        // Truthful only if the token endpoint would actually honor it: the
        // refresh grant refuses banned/deleted users, so introspection must
        // not report those tokens as active.
        else if (tokenValue.Length > 0
            && await store.FindRefreshTokenAsync(SecureTokens.Sha256Hex(tokenValue), ct) is { } refresh
            && refresh.InstanceId == instance.Id
            && refresh.ClientId == authentication.Client.ClientId
            && refresh.Status == "active"
            && refresh.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            && await store.GetUserAsync(refresh.UserId, ct) is { Banned: false, Deleted: false })
        {
            return Results.Json(new Dictionary<string, object>
            {
                ["active"] = true,
                ["token_type"] = "refresh_token",
                ["sub"] = refresh.UserId,
                ["client_id"] = refresh.ClientId,
                ["scope"] = string.Join(' ', refresh.Scopes),
                ["exp"] = refresh.ExpiresAt,
                ["iat"] = refresh.CreatedAt,
            });
        }
        return Results.Json(new { active = false });
    }

    private static long? ClaimLong(Dictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value)
            && value is JsonElement { ValueKind: JsonValueKind.Number } element
            && element.TryGetInt64(out var parsed)
                ? parsed
                : null;
}
