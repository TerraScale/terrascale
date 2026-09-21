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

internal sealed class DeletePasskeyEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/v1/{slug}/users/{userId}/passkeys/{passkeyId}", HandleAsync);

    private static async Task<IResult> HandleAsync(
        string slug, string userId, string passkeyId, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(request, ct);
        if (context is null)
            return ApiResults.Unauthorized();
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null || instance.Id != context.Instance.Id)
            return ApiResults.Unauthorized();

        var passkey = await store.GetPasskeyAsync(passkeyId, ct);
        if (passkey is null || passkey.InstanceId != instance.Id || passkey.UserId != userId)
            return ApiResults.NotFound("passkey_not_found", "unknown passkey");

        await store.DeletePasskeyAsync(passkey.Id, ct);
        await AuditAsync(store, instance.Id, "passkey.deleted", context.ApiKeyId,
            new Dictionary<string, object> { ["passkeyId"] = passkey.Id, ["userId"] = userId }, ct);
        return Results.NoContent();
    }
}
