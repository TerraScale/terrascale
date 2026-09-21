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

internal sealed class GetPasskeysPageEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/passkeys/manage", HandleAsync);

    // ---- Self-service management page --------------------------------------------------

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ApiResults.NotFound("instance_not_found", "unknown instance");
        if (await SessionUserAsync(request, sessions, instance.Id, ct) is not { } user)
            return ApiResults.Unauthorized("session_required", "A valid session is required.");

        var passkeys = await store.ListPasskeysAsync(user.Id, ct);
        return PasskeyPages.Manage(instance.Slug, user.Name ?? user.Email ?? user.Id, passkeys);
    }
}
