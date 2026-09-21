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

internal sealed class VerifyAssertionEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/passkeys/assert/verify", HandleAsync);

    private static async Task<IResult> HandleAsync(
        string slug, HttpRequest request, HttpResponse response, AuthStore store, SessionService sessions, AuthOptions options, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ApiResults.NotFound("instance_not_found", "unknown instance");

        var body = await ReadJsonAsync<PasskeyAssertionRequest>(request, ct);
        if (body?.Response is null || string.IsNullOrEmpty(body.Id)
            || string.IsNullOrEmpty(body.Response.ClientDataJSON) || string.IsNullOrEmpty(body.Response.AuthenticatorData)
            || string.IsNullOrEmpty(body.Response.Signature))
            return ApiResults.BadRequest("invalid_request", "id, clientDataJSON, authenticatorData, and signature are required");

        string clientDataJson;
        byte[] authenticatorData;
        byte[] signature;
        byte[] credentialId;
        try
        {
            clientDataJson = Encoding.UTF8.GetString(Encodings.Base64UrlDecode(body.Response.ClientDataJSON));
            authenticatorData = Encodings.Base64UrlDecode(body.Response.AuthenticatorData);
            signature = Encodings.Base64UrlDecode(body.Response.Signature);
            credentialId = Encodings.Base64UrlDecode(body.Id);
        }
        catch (FormatException)
        {
            return ApiResults.BadRequest("invalid_request", "malformed base64url field");
        }

        var credentialKey = Encodings.Base64UrlEncode(credentialId);
        var passkey = await store.FindPasskeyByCredentialIdAsync(credentialKey, ct);
        if (passkey is null || passkey.InstanceId != instance.Id)
            return ApiResults.Unauthorized("unknown_credential", "unknown credential");

        var challenge = ClientDataChallenge(clientDataJson);
        if (challenge is null)
            return ApiResults.BadRequest("invalid_request", "clientDataJSON is malformed or has no challenge");
        if (!await PasskeyGate.ConsumeChallengeAsync(store, instance.Id, "webauthn.assert", challenge, ct))
            return ApiResults.Unauthorized("invalid_challenge", "unknown, expired, or already-used challenge");

        var user = await store.GetUserAsync(passkey.UserId, ct);
        if (user is null || user.Deleted || user.Banned)
            return ApiResults.Unauthorized("unknown_credential", "unknown credential");

        WebAuthnVerifier.AssertionResult assertion;
        try
        {
            var credentialPublicKey = CoseKey.FromStored(Convert.FromBase64String(passkey.PublicKeyCborBase64));
            assertion = WebAuthnVerifier.VerifyAssertion(
                clientDataJson, authenticatorData, signature, challenge, options.PublicOrigin.Host, Origin(options),
                credentialId, credentialPublicKey, passkey.SignCount);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            return ApiResults.Error(401, "assertion_failed", exception.Message);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await store.UpsertPasskeyAsync(passkey with { SignCount = assertion.SignCount, LastUsedAt = now }, ct);
        var (_, token) = await sessions.CreateSessionAsync(
            instance.Id, user.Id, request.Headers.UserAgent.ToString(), request.HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        response.Cookies.Append(SessionCookies.Name, token, SessionCookies.Options);
        await store.UpsertUserAsync(user with { LastSignInAt = now, UpdatedAt = now }, ct);
        await AuditAsync(store, instance.Id, "user.signed_in", user.Id,
            new Dictionary<string, object> { ["method"] = "passkey", ["passkeyId"] = passkey.Id }, ct);
        return Results.Ok(new { redirect = $"/ui/{slug}/account" });
    }
}
