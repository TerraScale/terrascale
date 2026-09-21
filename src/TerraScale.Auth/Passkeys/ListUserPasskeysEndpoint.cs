namespace TerraScale.Auth.Passkeys;

using System.Net;
using System.Text;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.Auth.WebAuthn;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Passkeys.PasskeySupport;

internal sealed class ListUserPasskeysEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/users/{userId}/passkeys", HandleAsync);

    // ---- Management API ------------------------------------------------------------------

    private static async Task<IResult> HandleAsync(
        string slug, string userId, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(request, ct);
        if (context is null)
            return ApiResults.Unauthorized();
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null || instance.Id != context.Instance.Id)
            return ApiResults.Unauthorized();
        var user = await store.GetUserAsync(userId, ct);
        if (user is null || user.InstanceId != instance.Id)
            return ApiResults.NotFound("user_not_found", "unknown user");

        var passkeys = await store.ListPasskeysAsync(userId, ct);
        return Results.Json(new
        {
            passkeys = passkeys.Select(p => new { p.Id, p.Nickname, p.Algorithm, p.CreatedAt, p.LastUsedAt }).ToArray(),
        });
    }
}
