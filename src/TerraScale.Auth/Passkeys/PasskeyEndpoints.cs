namespace TerraScale.Auth.Passkeys;

using System.Text;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.Auth.WebAuthn;

/// <summary>Passkey feature endpoints: WebAuthn registration, discoverable-credential
/// sign-in, self-service management page, and the management API.</summary>
public static class PasskeyEndpoints
{
    private const int TimeoutMs = 300_000;

    /// <summary>Maps Passkeys endpoints.</summary>
    public static void Map(WebApplication app)
    {
        app.MapPost("/ui/{slug}/passkeys/register/options", RegisterOptionsAsync);
        app.MapPost("/ui/{slug}/passkeys/register", RegisterAsync);
        app.MapGet("/ui/{slug}/passkeys/assert", (string slug) => PasskeyPages.Assert(slug));
        app.MapPost("/ui/{slug}/passkeys/assert/options", AssertOptionsAsync);
        app.MapPost("/ui/{slug}/passkeys/assert/verify", AssertVerifyAsync);
        app.MapGet("/ui/{slug}/passkeys/manage", ManageAsync);
        app.MapPost("/ui/{slug}/passkeys/manage/{id}/delete", ManageDeleteAsync);
        app.MapGet("/v1/{slug}/users/{userId}/passkeys", ListForUserAsync);
        app.MapDelete("/v1/{slug}/users/{userId}/passkeys/{passkeyId}", DeleteAsync);
    }

    // ---- Registration ceremony -------------------------------------------------------

    private static async Task<IResult> RegisterOptionsAsync(
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

    private static async Task<IResult> RegisterAsync(
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
        await store.CreatePasskeyAsync(passkey, ct);
        await AuditAsync(store, instance.Id, "passkey.created", user.Id,
            new Dictionary<string, object> { ["passkeyId"] = passkey.Id, ["nickname"] = passkey.Nickname }, ct);
        return Results.Ok(new { ok = true });
    }

    // ---- Assertion ceremony (discoverable sign-in) -------------------------------------

    private static async Task<IResult> AssertOptionsAsync(string slug, AuthStore store, AuthOptions options, CancellationToken ct)
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

    private static async Task<IResult> AssertVerifyAsync(
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

    // ---- Self-service management page --------------------------------------------------

    private static async Task<IResult> ManageAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ApiResults.NotFound("instance_not_found", "unknown instance");
        if (await SessionUserAsync(request, sessions, instance.Id, ct) is not { } user)
            return ApiResults.Unauthorized("session_required", "A valid session is required.");

        var passkeys = await store.ListPasskeysAsync(user.Id, ct);
        return PasskeyPages.Manage(instance.Slug, user.Name ?? user.Email ?? user.Id, passkeys);
    }

    private static async Task<IResult> ManageDeleteAsync(
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

    // ---- Management API ------------------------------------------------------------------

    private static async Task<IResult> ListForUserAsync(
        string slug, string userId, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(request, ct);
        if (context is null)
            return ApiResults.Unauthorized();
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null || instance.Id != context.Instance.Id)
            return ApiResults.NotFound("instance_not_found", "unknown instance");
        var user = await store.GetUserAsync(userId, ct);
        if (user is null || user.InstanceId != instance.Id)
            return ApiResults.NotFound("user_not_found", "unknown user");

        var passkeys = await store.ListPasskeysAsync(userId, ct);
        return Results.Json(new
        {
            passkeys = passkeys.Select(p => new { p.Id, p.Nickname, p.Algorithm, p.CreatedAt, p.LastUsedAt }).ToArray(),
        });
    }

    private static async Task<IResult> DeleteAsync(
        string slug, string userId, string passkeyId, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(request, ct);
        if (context is null)
            return ApiResults.Unauthorized();
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null || instance.Id != context.Instance.Id)
            return ApiResults.NotFound("instance_not_found", "unknown instance");

        var passkey = await store.GetPasskeyAsync(passkeyId, ct);
        if (passkey is null || passkey.InstanceId != instance.Id || passkey.UserId != userId)
            return ApiResults.NotFound("passkey_not_found", "unknown passkey");

        await store.DeletePasskeyAsync(passkey.Id, ct);
        await AuditAsync(store, instance.Id, "passkey.deleted", context.ApiKeyId,
            new Dictionary<string, object> { ["passkeyId"] = passkey.Id, ["userId"] = userId }, ct);
        return Results.NoContent();
    }

    // ---- Shared helpers --------------------------------------------------------------------

    private static string Origin(AuthOptions options) => options.PublicOrigin.AbsoluteUri.TrimEnd('/');

    private static async Task<User?> SessionUserAsync(HttpRequest request, SessionService sessions, string instanceId, CancellationToken ct)
    {
        var token = request.Cookies[SessionCookies.Name];
        if (string.IsNullOrEmpty(token))
            return null;
        return (await sessions.ResolveAsync(instanceId, token, ct))?.User;
    }

    private static string? ClientDataChallenge(string clientDataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(clientDataJson);
            return document.RootElement.TryGetProperty("challenge", out var challenge) ? challenge.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        try
        {
            return await request.ReadFromJsonAsync<T>(ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task AuditAsync(AuthStore store, string instanceId, string type, string? actorId, Dictionary<string, object> data, CancellationToken ct) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Type = type,
            ActorId = actorId,
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
}

/// <summary>Registration ceremony request body.</summary>
public sealed record PasskeyRegistrationRequest(string Id, PasskeyRegistrationResponse Response, string? Nickname);

/// <summary>Registration ceremony response fields.</summary>
public sealed record PasskeyRegistrationResponse(string ClientDataJSON, string AttestationObject, string[]? Transports);

/// <summary>Assertion ceremony request body.</summary>
public sealed record PasskeyAssertionRequest(string Id, PasskeyAssertionResponse Response);

/// <summary>Assertion ceremony response fields.</summary>
public sealed record PasskeyAssertionResponse(string ClientDataJSON, string AuthenticatorData, string Signature, string? UserHandle);
