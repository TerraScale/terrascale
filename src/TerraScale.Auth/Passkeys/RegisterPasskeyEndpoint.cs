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

internal sealed class RegisterPasskeyEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/passkeys/register", HandleAsync);

    private static async Task<IResult> HandleAsync(
        string slug, HttpRequest request, AuthStore store, SessionService sessions, AuthOptions options, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ApiResults.NotFound("instance_not_found", "unknown instance");
        if (await SessionUserAsync(request, sessions, instance.Id, ct) is not { } user)
            return ApiResults.Unauthorized("session_required", "A valid session is required.");

        var body = await ReadJsonAsync<PasskeyRegistrationRequest>(request, ct);
        if (body?.Response is null || string.IsNullOrEmpty(body.Id)
            || string.IsNullOrEmpty(body.Response.ClientDataJSON) || string.IsNullOrEmpty(body.Response.AttestationObject))
            return ApiResults.BadRequest("invalid_request", "id, clientDataJSON, and attestationObject are required");

        string clientDataJson;
        byte[] attestationObject;
        byte[] credentialId;
        try
        {
            clientDataJson = Encoding.UTF8.GetString(Encodings.Base64UrlDecode(body.Response.ClientDataJSON));
            attestationObject = Encodings.Base64UrlDecode(body.Response.AttestationObject);
            credentialId = Encodings.Base64UrlDecode(body.Id);
        }
        catch (FormatException)
        {
            return ApiResults.BadRequest("invalid_request", "malformed base64url field");
        }

        var challenge = ClientDataChallenge(clientDataJson);
        if (challenge is null)
            return ApiResults.BadRequest("invalid_request", "clientDataJSON is malformed or has no challenge");
        if (!await PasskeyGate.ConsumeChallengeAsync(store, instance.Id, "webauthn.register", challenge, ct))
            return ApiResults.BadRequest("invalid_challenge", "unknown, expired, or already-used challenge");

        var credentialKey = Encodings.Base64UrlEncode(credentialId);
        if (await store.FindPasskeyByCredentialIdAsync(credentialKey, ct) is not null)
            return ApiResults.Conflict("credential_exists", "this credential is already registered");

        (byte[] VerifiedId, CoseKey PublicKey, uint SignCount) verified;
        try
        {
            verified = WebAuthnVerifier.VerifyRegistration(
                clientDataJson, attestationObject, challenge, options.PublicOrigin.Host, Origin(options));
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            return ApiResults.BadRequest("registration_failed", exception.Message);
        }
        if (!verified.VerifiedId.AsSpan().SequenceEqual(credentialId))
            return ApiResults.BadRequest("invalid_request", "credential id does not match attested credential data");

        var passkey = new Passkey
        {
            Id = "pky_" + SecureTokens.RandomToken(20),
            InstanceId = instance.Id,
            UserId = user.Id,
            CredentialKey = credentialKey,
            PublicKeyCborBase64 = Convert.ToBase64String(verified.PublicKey.RawCbor),
            Algorithm = verified.PublicKey.Algorithm,
            SignCount = verified.SignCount,
            Transports = body.Response.Transports is { Length: > 0 } transports ? string.Join(",", transports) : null,
            Nickname = string.IsNullOrWhiteSpace(body.Nickname) ? "Passkey" : body.Nickname.Trim(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        try
        {
            await store.CreatePasskeyAsync(passkey, ct);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            // The credential-id pre-check lost a race; the atomic claim is the authority.
            return ApiResults.Conflict("credential_exists", "this credential is already registered");
        }
        await AuditAsync(store, instance.Id, "passkey.created", user.Id,
            new Dictionary<string, object> { ["passkeyId"] = passkey.Id, ["nickname"] = passkey.Nickname }, ct);
        return Results.Ok(new { ok = true });
    }
}
