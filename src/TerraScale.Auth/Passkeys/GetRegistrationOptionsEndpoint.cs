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

internal sealed class GetRegistrationOptionsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/passkeys/register/options", HandleAsync);

    // ---- Registration ceremony -------------------------------------------------------

    private static async Task<IResult> HandleAsync(
        string slug, HttpRequest request, AuthStore store, SessionService sessions, AuthOptions options, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ApiResults.NotFound("instance_not_found", "unknown instance");
        if (await SessionUserAsync(request, sessions, instance.Id, ct) is not { } user)
            return ApiResults.Unauthorized("session_required", "A valid session is required.");

        var challenge = await PasskeyGate.IssueChallengeAsync(store, instance.Id, "webauthn.register", user.Id, ct);
        var existing = await store.ListPasskeysAsync(user.Id, ct);
        return Results.Json(new
        {
            publicKey = new
            {
                challenge,
                rp = new { id = options.PublicOrigin.Host, name = "ts-auth" },
                user = new
                {
                    id = Encodings.Base64UrlEncode(Encoding.UTF8.GetBytes(user.Id)),
                    name = user.Email ?? user.Id,
                    displayName = user.Name ?? user.Email ?? user.Id,
                },
                pubKeyCredParams = new object[]
                {
                    new { type = "public-key", alg = -7 },
                    new { type = "public-key", alg = -257 },
                },
                timeout = TimeoutMs,
                excludeCredentials = existing.Select(p => new { type = "public-key", id = p.CredentialKey }).ToArray(),
                authenticatorSelection = new { residentKey = "required", userVerification = "preferred" },
                attestation = "none",
            },
        });
    }
}
