using Dapper;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fido2NetLib;
using Fido2NetLib.Objects;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.WebAuthn;

public readonly union PasskeyRenameResult(PasskeyCredentialEntry, Forbidden, NotFound, Conflict);
public readonly union PasskeyDeleteResult(PasskeyCredentialEntry, Forbidden, NotFound, Conflict);
public readonly union PasskeyVerifyResult(PasskeyCredentialEntry, Forbidden);

public readonly union WriteCheck(Done, Conflict);

public sealed class PasskeyCredentials(AuthData data, Credentials credentials, Users users, AttemptLimiter limiter, Fido2NetLib.Fido2 fido2, AuthOptions options, TimeProvider clock, IEmailSender email)
{
    public const string CredentialKind = "passkey_credential";
    private const string ChallengeKind = "passkey-challenge";
    private const string SignInChallengeKind = "passkey-signin-challenge";
    private const int MaxPasskeys = Credentials.MaxBindings;

    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(15);

    public async Task<(CredentialCreateOptions Options, string Challenge)?> OptionsAsync(UserRecord caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await this.CurrentUserAsync(caller, cancellationToken) is not { } user)
        {
            return null;
        }

        var entries = await this.ReadAsync(user.InstanceId, user.Id, cancellationToken);
        var creation = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = Encoding.UTF8.GetBytes(user.Id), Name = user.Email, DisplayName = user.Email },
            ExcludeCredentials = [.. entries.Select(entry => new Fido2NetLib.Objects.PublicKeyCredentialDescriptor(Base64UrlDecode(entry.Value.CredentialId)))],
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = Fido2NetLib.Objects.ResidentKeyRequirement.Preferred,
                UserVerification = Fido2NetLib.Objects.UserVerificationRequirement.Required,
            },
            AttestationPreference = Fido2NetLib.Objects.AttestationConveyancePreference.None,
        });
        var challenge = new PasskeyChallenge(AuthIds.New(), user.Id, user.AuthEpoch, user.AuthStamp ?? string.Empty,
            clock.GetUtcNow() + ChallengeLifetime, creation.ToJson());
        return (creation, SecretBox.Seal(options.MasterKey, JsonSerializer.SerializeToUtf8Bytes(challenge, WebAuthnJson.Default.PasskeyChallenge),
            Context(user.InstanceId)));
    }

    public async Task<PasskeyRenameResult> RenameAsync(UserRecord caller, string credentialId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        if (await this.CurrentUserAsync(caller, cancellationToken) is not { } user)
        {
            return new Forbidden("Sign in again before renaming a passkey.", "passkey_denied");
        }

        var entry = (await this.ReadAsync(user.InstanceId, user.Id, cancellationToken)).FirstOrDefault(item => item.Value.CredentialId == credentialId);
        if (entry is null)
        {
            return new NotFound("No passkey with this id is enrolled for the account.", "passkey_not_found");
        }

        var renamed = entry.Value with { Name = name };
        return await this.WriteAsync([Change.Assert(user), Change.Update(entry, renamed)], cancellationToken) switch
        {
            Done => renamed,
            Conflict conflict => conflict,
        };
    }

    public async Task<PasskeyDeleteResult> DeleteAsync(UserRecord caller, string credentialId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        if (await this.CurrentUserAsync(caller, cancellationToken) is not { } user)
        {
            return new Forbidden("Sign in again before deleting a passkey.", "passkey_denied");
        }

        var entry = (await this.ReadAsync(user.InstanceId, user.Id, cancellationToken)).FirstOrDefault(item => item.Value.CredentialId == credentialId);
        if (entry is null)
        {
            return new NotFound("No passkey with this id is enrolled for the account.", "passkey_not_found");
        }

        return await this.WriteAsync([Change.Assert(user), Change.Delete(entry)], cancellationToken) switch
        {
            Done => entry.Value,
            Conflict conflict => conflict,
        };
    }

    private async Task<WriteCheck> WriteAsync(IReadOnlyList<Change> changes, CancellationToken cancellationToken) =>
        await data.SaveAsync(changes, cancellationToken)
            ? new Done()
            : new Conflict("The passkey list changed; read it again and retry.", "version_conflict");

    public async Task<IReadOnlyList<PasskeyCredentialEntry>> ListAsync(UserRecord caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return [.. (await this.ReadAsync(caller.InstanceId, caller.Id, cancellationToken)).Select(entry => entry.Value)];
    }

    public async Task<PasskeyVerifyResult> VerifyAsync(UserRecord caller, string? sealedChallenge, AuthenticatorAttestationRawResponse response, string? name, ChallengedSignIn? signIn = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(response);
        if (await limiter.AcquireAsync(caller.InstanceId, "passkey-enroll", caller.Id, 10, AttemptWindow, cancellationToken) is { } wait)
        {
            throw ApiException.RateLimited("Too many passkey enrollment attempts; retry after the Retry-After delay.", wait);
        }

        if (this.OpenChallenge(caller.InstanceId, sealedChallenge) is not { } challenge
            || await this.CurrentUserAsync(caller, cancellationToken) is not { } user
            || user.AuthEpoch != challenge.AuthEpoch || user.AuthStamp != challenge.AuthStamp
            || await credentials.RequireAsync(caller.InstanceId, [.. caller.AuthenticationBindings.Where(binding => binding != signIn?.Completion)], cancellationToken) is not { } lineage)
        {
            return DeniedEnroll();
        }

        var completion = Credentials.New(ChallengeKind, challenge.Id);
        if (await credentials.IsClaimedAsync(caller.InstanceId, completion, cancellationToken)
            || response.RawId is not { Length: > 0 and <= 1024 } rawId
            || (await this.ReadAsync(user.InstanceId, user.Id, cancellationToken)).Count >= MaxPasskeys)
        {
            return DeniedEnroll();
        }

        Fido2NetLib.Objects.RegisteredPublicKeyCredential registered;
        try
        {
            registered = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = CredentialCreateOptions.FromJson(challenge.OptionsJson),
                IsCredentialIdUniqueToUserCallback = async (parameters, token) =>
                    !await this.ExistsAsync(caller.InstanceId, Base64UrlEncode(parameters.CredentialId), token),
            }, cancellationToken);
        }
        catch (Exception error) when (error is Fido2VerificationException or ArgumentException or InvalidOperationException or FormatException or JsonException)
        {
            return DeniedEnroll();
        }

        var entry = new PasskeyCredentialEntry(Base64UrlEncode(rawId), registered.PublicKey, registered.SignCount,
            [.. registered.Transports.Select(transport => transport.ToEnumMemberValue())], name, registered.AaGuid.ToString(), clock.GetUtcNow(), null)
        {
            InstanceId = user.InstanceId,
            UserId = user.Id,
        };
        List<Change> changes = [.. lineage, Change.Assert(user), Change.Insert(entry), Credentials.Claim(user.InstanceId, completion)];
        if (signIn is not null)
        {
            if (signIn.ExpiresAt <= clock.GetUtcNow())
            {
                return DeniedEnroll();
            }

            changes.Add(Credentials.Claim(user.InstanceId, signIn.Completion));
        }

        if (!await data.SaveAsync(changes, cancellationToken))
        {
            return DeniedEnroll();
        }

        await FactorEnrollmentNotice.SendAsync(email, user, cancellationToken);
        return entry;
    }

    public async Task<(AssertionOptions Options, string Challenge)?> SignInOptionsAsync(string instanceId, string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var user = await users.FindByEmailAsync(instanceId, email, cancellationToken);
        var entries = user is null ? [] : await this.ReadAsync(instanceId, user.Id, cancellationToken);
        var assertion = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [.. entries.Select(entry => new Fido2NetLib.Objects.PublicKeyCredentialDescriptor(Base64UrlDecode(entry.Value.CredentialId)))],
            UserVerification = Fido2NetLib.Objects.UserVerificationRequirement.Required,
        });
        var challenge = new PasskeySignInChallenge(AuthIds.New(), user?.Id ?? string.Empty, user?.AuthEpoch ?? 0, user?.AuthStamp ?? string.Empty,
            clock.GetUtcNow() + ChallengeLifetime, assertion.ToJson());
        return (assertion, SecretBox.Seal(options.MasterKey, JsonSerializer.SerializeToUtf8Bytes(challenge, WebAuthnJson.Default.PasskeySignInChallenge),
            Context(instanceId)));
    }

    public async Task<UserRecord?> VerifySignInAsync(string instanceId, string? sealedChallenge, string email, AuthenticatorAssertionRawResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        ArgumentNullException.ThrowIfNull(response);
        var address = email.Trim();
        if (await limiter.AcquireAsync(instanceId, "passkey-signin", Users.NormalizeEmail(address) ?? address, 10, AttemptWindow, cancellationToken) is { } wait)
        {
            throw ApiException.RateLimited("Too many passkey sign-in attempts; retry after the Retry-After delay.", wait);
        }

        if (this.OpenSignInChallenge(instanceId, sealedChallenge) is not { } challenge
            || response.RawId is not { Length: > 0 and <= 1024 })
        {
            return null;
        }

        var credentialId = Base64UrlEncode(response.RawId);
        var completion = Credentials.New(SignInChallengeKind, challenge.Id);
        if (challenge.UserId.Length == 0
            || await credentials.IsClaimedAsync(instanceId, completion, cancellationToken)
            || await users.GetAsync(instanceId, challenge.UserId, cancellationToken) is not { Disabled: false } user
            || user.AuthEpoch != challenge.AuthEpoch || user.AuthStamp != challenge.AuthStamp
            || (await this.ReadAsync(instanceId, user.Id, cancellationToken)).FirstOrDefault(item => item.Value.CredentialId == credentialId) is not { } entry)
        {
            return null;
        }

        VerifyAssertionResult verified;
        try
        {
            verified = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = AssertionOptions.FromJson(challenge.OptionsJson),
                StoredPublicKey = entry.Value.PublicKeyCose,
                StoredSignatureCounter = entry.Value.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (parameters, token) =>
                    Task.FromResult(Base64UrlEncode(parameters.CredentialId) == credentialId
                        && (parameters.UserHandle is not { Length: > 0 } handle || handle.SequenceEqual(Encoding.UTF8.GetBytes(user.Id)))),
            }, cancellationToken);
        }
        catch (Exception error) when (error is Fido2VerificationException or ArgumentException or InvalidOperationException or FormatException or JsonException)
        {
            return null;
        }

        var used = entry.Value with { SignCount = verified.SignCount, LastUsedAt = clock.GetUtcNow() };
        return await data.SaveAsync([Change.Assert(user), Change.Update(entry, used), Credentials.Claim(user.InstanceId, completion)], cancellationToken)
            ? user with { MultiFactor = true, MfaMethod = "passkey", AuthenticationBindings = [completion] }
            : null;
    }

    internal static Forbidden DeniedEnroll() => new("The passkey registration failed verification; start again.", "passkey_denied");

    public async Task<bool> EnrolledAsync(string instanceId, string userId, CancellationToken cancellationToken = default) =>
        await data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM passkeys WHERE instance_id = @instanceId AND user_id = @userId)", new { instanceId, userId }), cancellationToken);

    private PasskeySignInChallenge? OpenSignInChallenge(string instanceId, string? sealedChallenge)
    {
        PasskeySignInChallenge? challenge;
        try
        {
            challenge = sealedChallenge is { Length: > 0 and <= 16384 }
                ? JsonSerializer.Deserialize(SecretBox.Open(options.MasterKey, sealedChallenge, Context(instanceId)), WebAuthnJson.Default.PasskeySignInChallenge)
                : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        {
            return null;
        }

        return challenge is not null
            && challenge.ExpiresAt > clock.GetUtcNow()
            && TerraScaleIds.IsValid(challenge.Id)
            && (challenge.UserId.Length == 0 || TerraScaleIds.IsValid(challenge.UserId))
            && challenge.AuthEpoch >= 0
            ? challenge : null;
    }

    private PasskeyChallenge? OpenChallenge(string instanceId, string? sealedChallenge)
    {
        PasskeyChallenge? challenge;
        try
        {
            challenge = sealedChallenge is { Length: > 0 and <= 16384 }
                ? JsonSerializer.Deserialize(SecretBox.Open(options.MasterKey, sealedChallenge, Context(instanceId)), WebAuthnJson.Default.PasskeyChallenge)
                : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        {
            return null;
        }

        return challenge is not null
            && challenge.ExpiresAt > clock.GetUtcNow()
            && TerraScaleIds.IsValid(challenge.UserId)
            && TerraScaleIds.IsValid(challenge.Id)
            && challenge.AuthEpoch >= 0
            ? challenge : null;
    }


    private async Task<UserRecord?> CurrentUserAsync(UserRecord caller, CancellationToken cancellationToken) =>
        await users.GetAsync(caller.InstanceId, caller.Id, cancellationToken) is { } current
            && current.AuthEpoch == caller.AuthEpoch && current.AuthStamp == caller.AuthStamp ? current : null;

    private async Task<IReadOnlyList<Versioned<PasskeyCredentialEntry>>> ReadAsync(string instanceId, string userId, CancellationToken cancellationToken) =>
        Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<PasskeyCredentialEntry>(
            "SELECT * FROM passkeys WHERE instance_id = @instanceId AND user_id = @userId ORDER BY created_at, credential_id", new { instanceId, userId }), cancellationToken));

    private Task<bool> ExistsAsync(string instanceId, string credentialId, CancellationToken cancellationToken) =>
        data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM passkeys WHERE instance_id = @instanceId AND credential_id = @credentialId)", new { instanceId, credentialId }), cancellationToken);

    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '='));
    }

    private static string Context(string instanceId) => "passkey-challenge:" + instanceId;
}

internal sealed record PasskeyChallenge(string Id, string UserId, long AuthEpoch, string AuthStamp, DateTimeOffset ExpiresAt, string OptionsJson);

internal sealed record PasskeySignInChallenge(string Id, string UserId, long AuthEpoch, string AuthStamp, DateTimeOffset ExpiresAt, string OptionsJson);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PasskeyChallenge))]
[JsonSerializable(typeof(PasskeySignInChallenge))]
[JsonSerializable(typeof(PasskeyOptionsRequest))]
[JsonSerializable(typeof(PasskeyOptionsBody))]
[JsonSerializable(typeof(PasskeyVerifyRequest))]
[JsonSerializable(typeof(PasskeyEnrolledBody))]
[JsonSerializable(typeof(PasskeySignInRequest))]
[JsonSerializable(typeof(PasskeyItemBody))]
[JsonSerializable(typeof(PasskeyListBody))]
[JsonSerializable(typeof(PasskeyRenameRequest))]
[JsonSerializable(typeof(ErrorBody))]
internal sealed partial class WebAuthnJson : JsonSerializerContext;

public sealed record PasskeyOptionsRequest;

public sealed record PasskeyOptionsBody(string Challenge, JsonElement Options);

public sealed record PasskeyVerifyRequest(string? Challenge, string? Name, PasskeyAttestationRequest? Response);

public sealed record PasskeyAttestationRequest(string? Id, string? RawId, string? Type, PasskeyAttestationInnerRequest? Response);

public sealed record PasskeyAttestationInnerRequest(
    [property: JsonPropertyName("clientDataJSON")] string? ClientDataJson,
    string? AttestationObject,
    IReadOnlyList<string>? Transports);

public sealed record PasskeySignInRequest(string? Challenge, string? Email, string? Next = null, PasskeyAssertionRequest? Response = null);

public sealed record PasskeyAssertionRequest(string? Id, [property: JsonPropertyName("rawId")] string? RawId, string? Type, PasskeyAssertionInnerRequest? Response);

public sealed record PasskeyAssertionInnerRequest(
    [property: JsonPropertyName("clientDataJSON")] string? ClientDataJson,
    [property: JsonPropertyName("authenticatorData")] string? AuthenticatorData,
    string? Signature,
    [property: JsonPropertyName("userHandle")] string? UserHandle = null);

public sealed record PasskeyEnrolledBody(string Id, string? Name, string Aaguid, DateTimeOffset CreatedAt);

public sealed record PasskeyItemBody(string Id, string? Name, string Aaguid, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

public sealed record PasskeyListBody(IReadOnlyList<PasskeyItemBody> Passkeys);

public sealed record PasskeyRenameRequest(string? Name);
