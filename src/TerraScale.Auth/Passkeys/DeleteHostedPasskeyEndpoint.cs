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

internal sealed class DeleteHostedPasskeyEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/passkeys/manage/{id}/delete", HandleAsync);

    private static async Task<IResult> HandleAsync(
        string slug, string id, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ApiResults.NotFound("instance_not_found", "unknown instance");
        if (await SessionUserAsync(request, sessions, instance.Id, ct) is not { } user)
            return ApiResults.Unauthorized("session_required", "A valid session is required.");

        var passkey = await store.GetPasskeyAsync(id, ct);
        if (passkey is null || passkey.InstanceId != instance.Id || passkey.UserId != user.Id)
            return ApiResults.NotFound("passkey_not_found", "unknown passkey");

        await store.DeletePasskeyAsync(passkey.Id, ct);
        await AuditAsync(store, instance.Id, "passkey.deleted", user.Id,
            new Dictionary<string, object> { ["passkeyId"] = passkey.Id, ["userId"] = user.Id }, ct);
        return Results.Redirect($"/ui/{instance.Slug}/passkeys/manage");
    }
}
