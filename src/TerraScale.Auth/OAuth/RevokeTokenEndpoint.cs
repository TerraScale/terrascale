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

internal sealed class RevokeTokenEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/{slug}/oauth/revoke", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, CancellationToken ct)
    {
        if (await store.FindInstanceBySlugAsync(slug, ct) is not { } instance)
            return ApiResults.NotFound();
        var form = await request.ReadFormAsync(ct);
        var authentication = await AuthenticateClientAsync(request, form, store, instance.Id, ct);
        if (authentication.Client is null)
            return TokenError("invalid_client", authentication.Description ?? "client authentication failed", 401);

        // RFC 7009: unknown or already-invalid tokens still return 200; access
        // tokens are stateless JWTs, so only refresh tokens need store work.
        var tokenValue = form["token"].ToString();
        if (tokenValue.Length == 0)
            return TokenError("invalid_request", "token is required");
        if (await store.FindRefreshTokenAsync(SecureTokens.Sha256Hex(tokenValue), ct) is { } refresh
            && refresh.InstanceId == instance.Id
            && refresh.ClientId == authentication.Client.ClientId
            && refresh.Status == "active")
        {
            await store.UpsertRefreshTokenAsync(refresh with { Status = "revoked" }, ct);
        }
        return Results.Ok();
    }
}
