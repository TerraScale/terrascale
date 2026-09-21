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

internal sealed class GetAssertionOptionsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/passkeys/assert/options", HandleAsync);

    // ---- Assertion ceremony (discoverable sign-in) -------------------------------------

    private static async Task<IResult> HandleAsync(string slug, AuthStore store, AuthOptions options, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ApiResults.NotFound("instance_not_found", "unknown instance");

        var challenge = await PasskeyGate.IssueChallengeAsync(store, instance.Id, "webauthn.assert", null, ct);
        return Results.Json(new
        {
            publicKey = new { challenge, rpId = options.PublicOrigin.Host, timeout = TimeoutMs, userVerification = "preferred" },
        });
    }
}
