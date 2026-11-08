using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Fido2NetLib;
using Fido2NetLib.Objects;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.WebAuthn;

public sealed class PasskeyCredentials(AuthStore store, Users users, AttemptLimiter limiter, Fido2NetLib.Fido2 fido2, AuthOptions options, TimeProvider clock)
{
    public const string CredentialKind = "passkey_credential";
    private const string CredentialClaimKind = "credential:" + CredentialKind;
    private const string ChallengeKind = "passkey-challenge";
    private const string SignInChallengeKind = "passkey-signin-challenge";

    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(15);

    public async Task<(CredentialCreateOptions Options, string Challenge)?> OptionsAsync(UserRecord caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await this.CurrentUserAsync(caller, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            return null;
        }

        var entries = (await this.ReadAsync(user.User.InstanceId, user.User.Id, cancellationToken).ConfigureAwait(false))?.Record.Entries ?? [];
        var creation = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = Encoding.UTF8.GetBytes(user.User.Id), Name = user.User.Email, DisplayName = user.User.Email },
            ExcludeCredentials = [.. entries.Select(entry => new Fido2NetLib.Objects.PublicKeyCredentialDescriptor(Base64UrlDecode(entry.CredentialId)))],
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = Fido2NetLib.Objects.ResidentKeyRequirement.Preferred,
                UserVerification = Fido2NetLib.Objects.UserVerificationRequirement.Required,
            },
            AttestationPreference = Fido2NetLib.Objects.AttestationConveyancePreference.None,
        });
        var challenge = new PasskeyChallenge(AuthStore.NewId(), user.User.Id, user.User.AuthEpoch, user.User.AuthStamp ?? string.Empty,
            clock.GetUtcNow() + ChallengeLifetime, creation.ToJson());
        return (creation, SecretBox.Seal(options.MasterKey, JsonSerializer.SerializeToUtf8Bytes(challenge, WebAuthnJson.Default.PasskeyChallenge),
            Context(user.User.InstanceId)));
    }

    public async Task<PasskeyCredentialEntry?> RenameAsync(UserRecord caller, string credentialId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        if (await this.CurrentUserAsync(caller, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            return null;
        }

        if (await this.ReadAsync(user.User.InstanceId, user.User.Id, cancellationToken).ConfigureAwait(false) is not { } current
            || current.Record.Entries.FirstOrDefault(entry => entry.CredentialId == credentialId) is not { } entry)
        {
            throw NotFound();
        }

        var renamed = entry with { Name = name };
        var record = current.Record with
        {
            AuthEpoch = user.User.AuthEpoch,
            AuthStamp = user.User.AuthStamp,
            Entries = [.. current.Record.Entries.Select(existing => existing.CredentialId == credentialId ? renamed : existing)],
        };
        await this.WriteAsync(user, current, record, [], cancellationToken).ConfigureAwait(false);
        return renamed;
    }

    public async Task<PasskeyCredentialEntry?> DeleteAsync(UserRecord caller, string credentialId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        if (await this.CurrentUserAsync(caller, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            return null;
        }

        if (await this.ReadAsync(user.User.InstanceId, user.User.Id, cancellationToken).ConfigureAwait(false) is not { } current
            || current.Record.Entries.FirstOrDefault(entry => entry.CredentialId == credentialId) is not { } entry)
        {
            throw NotFound();
        }

        var claim = await store.FindUniqueAsync(user.User.InstanceId, CredentialClaimKind, credentialId, cancellationToken).ConfigureAwait(false);
        var record = current.Record with
        {
            AuthEpoch = user.User.AuthEpoch,
            AuthStamp = user.User.AuthStamp,
            Entries = [.. current.Record.Entries.Where(existing => existing.CredentialId != credentialId)],
        };
        var release = claim is { } held
            ? new[] { AuthStore.Release(user.User.InstanceId, CredentialClaimKind, credentialId, held.Version) }
            : Array.Empty<StoreOperation>();
        await this.WriteAsync(user, current, record, release, cancellationToken).ConfigureAwait(false);
        return entry;
    }

    private async Task<IReadOnlyList<PasskeyCredentialEntry>> WriteAsync((UserRecord User, string Version) user, (PasskeyCredentialRecord Record, string Version) current,
        PasskeyCredentialRecord record, IReadOnlyList<StoreOperation> extra, CancellationToken cancellationToken)
    {
        var epoch = new JsonObject
        {
            ["and"] = new JsonArray(
                new JsonObject { ["eq"] = new JsonArray("/instanceId", user.User.InstanceId) },
                new JsonObject { ["eq"] = new JsonArray("/id", user.User.Id) },
                new JsonObject { ["eq"] = new JsonArray("/authEpoch", user.User.AuthEpoch) }),
        };
        using var filter = JsonDocument.Parse(epoch.ToJsonString());
        var operations = new List<StoreOperation>
        {
            AuthStore.Require(AuthStore.UserTable, user.User.InstanceId, user.User.Id, filter.RootElement),
            AuthStore.Replace(AuthStore.PasskeyTable, user.User.InstanceId, user.User.Id,
                JsonSerializer.SerializeToElement(record, WebAuthnJson.Default.PasskeyCredentialRecord), current.Version),
        };
        operations.AddRange(extra);
        return await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is null
            ? record.Entries
            : throw new ApiException(StatusCodes.Status409Conflict, "version_conflict", "The passkey list changed; read it again and retry.");
    }

    private static ApiException NotFound() => new(StatusCodes.Status404NotFound, "passkey_not_found", "No passkey with this id is enrolled for the account.");

    public async Task<IReadOnlyList<PasskeyCredentialEntry>> ListAsync(UserRecord caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return (await this.ReadAsync(caller.InstanceId, caller.Id, cancellationToken).ConfigureAwait(false))?.Record.Entries ?? [];
    }

    public async Task<PasskeyCredentialEntry?> VerifyAsync(UserRecord caller, string? sealedChallenge, AuthenticatorAttestationRawResponse response, string? name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(response);
        if (await limiter.AcquireAsync(caller.InstanceId, "passkey-enroll", caller.Id, 10, AttemptWindow, cancellationToken).ConfigureAwait(false) is { } wait)
        {
            throw ApiException.RateLimited("Too many passkey enrollment attempts; retry after the Retry-After delay.", wait);
        }

        if (this.OpenChallenge(caller.InstanceId, sealedChallenge) is not { } challenge
            || await this.CurrentUserAsync(caller, cancellationToken).ConfigureAwait(false) is not { } user
            || user.User.AuthEpoch != challenge.AuthEpoch || user.User.AuthStamp != challenge.AuthStamp
            || !await store.AreCredentialsCurrentAsync(caller.InstanceId, caller.AuthenticationBindings, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var completion = AuthStore.NewCredentialBinding(ChallengeKind, challenge.Id);
        if (await store.FindUniqueAsync(caller.InstanceId, completion.Kind, completion.Key, cancellationToken).ConfigureAwait(false) is not null)
        {
            return null;
        }

        if (response.RawId is not { Length: > 0 and <= 1024 } rawId)
        {
            return null;
        }

        var credentialId = Base64UrlEncode(rawId);

        Fido2NetLib.Objects.RegisteredPublicKeyCredential registered;
        try
        {
            registered = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = CredentialCreateOptions.FromJson(challenge.OptionsJson),
                IsCredentialIdUniqueToUserCallback = async (parameters, token) =>
                    await store.FindUniqueAsync(caller.InstanceId, CredentialClaimKind, Base64UrlEncode(parameters.CredentialId), token).ConfigureAwait(false) is null,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is Fido2VerificationException or ArgumentException or InvalidOperationException or FormatException or JsonException)
        {
            return null;
        }

        var current = await this.ReadAsync(user.User.InstanceId, user.User.Id, cancellationToken).ConfigureAwait(false);
        var record = (current?.Record ?? new PasskeyCredentialRecord(user.User.InstanceId, user.User.Id, user.User.AuthEpoch, [])) with
        {
            AuthEpoch = user.User.AuthEpoch,
            AuthStamp = user.User.AuthStamp,
            Entries =
            [
                .. current?.Record.Entries.Where(entry => entry.CredentialId != credentialId) ?? [],
                new PasskeyCredentialEntry(credentialId, registered.PublicKey, registered.SignCount,
                    [.. registered.Transports.Select(transport => transport.ToEnumMemberValue())], name, registered.AaGuid.ToString(), clock.GetUtcNow(), null),
            ],
        };
        var serialized = JsonSerializer.SerializeToElement(record, WebAuthnJson.Default.PasskeyCredentialRecord);
        var epoch = new JsonObject
        {
            ["and"] = new JsonArray(
                new JsonObject { ["eq"] = new JsonArray("/instanceId", user.User.InstanceId) },
                new JsonObject { ["eq"] = new JsonArray("/id", user.User.Id) },
                new JsonObject { ["eq"] = new JsonArray("/authEpoch", user.User.AuthEpoch) }),
        };
        using var filter = JsonDocument.Parse(epoch.ToJsonString());
        var operations = new List<StoreOperation>
        {
            AuthStore.Require(AuthStore.UserTable, user.User.InstanceId, user.User.Id, filter.RootElement),
            current is { } existing
                ? AuthStore.Replace(AuthStore.PasskeyTable, user.User.InstanceId, user.User.Id, serialized, existing.Version)
                : AuthStore.Create(AuthStore.PasskeyTable, user.User.InstanceId, user.User.Id, serialized),
            AuthStore.CredentialClaim(user.User.InstanceId, AuthStore.NewCredentialBinding(CredentialKind, credentialId)),
            AuthStore.CredentialClaim(user.User.InstanceId, completion),
        };
        return await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is null
            ? record.Entries[^1]
            : null;
    }

    /// <summary>
    /// Assertion options for the hosted passkey sign-in step of <paramref name="email"/>: allowCredentials lists that
    /// user's credential ids and user verification is required. An unknown or empty address gets the same shape with no
    /// credential ids, so the step never reveals which addresses have accounts; the sealed challenge then fails
    /// generically at verification.
    /// </summary>
    public async Task<(AssertionOptions Options, string Challenge)?> SignInOptionsAsync(string instanceId, string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var user = await users.FindByEmailAsync(instanceId, email, cancellationToken).ConfigureAwait(false);
        var entries = user is null ? [] : (await this.ReadAsync(instanceId, user.Id, cancellationToken).ConfigureAwait(false))?.Record.Entries ?? [];
        var assertion = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [.. entries.Select(entry => new Fido2NetLib.Objects.PublicKeyCredentialDescriptor(Base64UrlDecode(entry.CredentialId)))],
            UserVerification = Fido2NetLib.Objects.UserVerificationRequirement.Required,
        });
        var challenge = new PasskeySignInChallenge(AuthStore.NewId(), user?.Id ?? string.Empty, user?.AuthEpoch ?? 0, user?.AuthStamp ?? string.Empty,
            clock.GetUtcNow() + ChallengeLifetime, assertion.ToJson());
        return (assertion, SecretBox.Seal(options.MasterKey, JsonSerializer.SerializeToUtf8Bytes(challenge, WebAuthnJson.Default.PasskeySignInChallenge),
            Context(instanceId)));
    }

    /// <summary>
    /// Verifies a hosted sign-in assertion (origin, RP id hash, challenge consumed once, user verification and signature;
    /// a non-increasing sign count is refused) and updates the credential's sign count and last use in the same
    /// transaction. The returned user carries the passkey's multi-factor proof for opening the session.
    /// </summary>
    public async Task<UserRecord?> VerifySignInAsync(string instanceId, string? sealedChallenge, string email, AuthenticatorAssertionRawResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        ArgumentNullException.ThrowIfNull(response);
        var address = email.Trim();
        if (await limiter.AcquireAsync(instanceId, "passkey-signin", Users.NormalizeEmail(address) ?? address, 10, AttemptWindow, cancellationToken).ConfigureAwait(false) is { } wait)
        {
            throw ApiException.RateLimited("Too many passkey sign-in attempts; retry after the Retry-After delay.", wait);
        }

        if (this.OpenSignInChallenge(instanceId, sealedChallenge) is not { } challenge
            || response.RawId is not { Length: > 0 and <= 1024 })
        {
            return null;
        }

        var credentialId = Base64UrlEncode(response.RawId);
        var completion = AuthStore.NewCredentialBinding(SignInChallengeKind, challenge.Id);
        if (challenge.UserId.Length == 0
            || await store.FindUniqueAsync(instanceId, completion.Kind, completion.Key, cancellationToken).ConfigureAwait(false) is not null
            || await users.GetVersionedAsync(instanceId, challenge.UserId, cancellationToken).ConfigureAwait(false) is not { User: { Disabled: false } user }
            || user.AuthEpoch != challenge.AuthEpoch || user.AuthStamp != challenge.AuthStamp
            || await this.ReadAsync(instanceId, user.Id, cancellationToken).ConfigureAwait(false) is not { } current
            || current.Record.Entries.FirstOrDefault(entry => entry.CredentialId == credentialId) is not { } entry)
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
                StoredPublicKey = entry.PublicKeyCose,
                StoredSignatureCounter = entry.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (parameters, token) =>
                    Task.FromResult(Base64UrlEncode(parameters.CredentialId) == credentialId
                        && (parameters.UserHandle is not { Length: > 0 } handle || handle.SequenceEqual(Encoding.UTF8.GetBytes(user.Id)))),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is Fido2VerificationException or ArgumentException or InvalidOperationException or FormatException or JsonException)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var record = current.Record with
        {
            Entries = [.. current.Record.Entries.Select(item => item.CredentialId == credentialId ? item with { SignCount = verified.SignCount, LastUsedAt = now } : item)],
        };
        var serialized = JsonSerializer.SerializeToElement(record, WebAuthnJson.Default.PasskeyCredentialRecord);
        var epoch = new JsonObject
        {
            ["and"] = new JsonArray(
                new JsonObject { ["eq"] = new JsonArray("/instanceId", user.InstanceId) },
                new JsonObject { ["eq"] = new JsonArray("/id", user.Id) },
                new JsonObject { ["eq"] = new JsonArray("/authEpoch", user.AuthEpoch) }),
        };
        using var filter = JsonDocument.Parse(epoch.ToJsonString());
        var operations = new List<StoreOperation>
        {
            AuthStore.Require(AuthStore.UserTable, user.InstanceId, user.Id, filter.RootElement),
            AuthStore.Replace(AuthStore.PasskeyTable, user.InstanceId, user.Id, serialized, current.Version),
            AuthStore.CredentialClaim(user.InstanceId, completion),
        };
        return await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is null
            ? user with { MultiFactor = true, MfaMethod = "passkey", AuthenticationBindings = [completion] }
            : null;
    }

    /// <summary>Whether the user has at least one enrolled passkey; unavailable state fails closed with 503.</summary>
    public async Task<bool> EnrolledAsync(string instanceId, string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await this.ReadAsync(instanceId, userId, cancellationToken).ConfigureAwait(false) is { Record.Entries.Count: > 0 };
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException)
        {
            throw ApiException.Unavailable(error);
        }
    }

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

    private async Task<(UserRecord User, string Version)?> CurrentUserAsync(UserRecord caller, CancellationToken cancellationToken) =>
        await users.GetVersionedAsync(caller.InstanceId, caller.Id, cancellationToken).ConfigureAwait(false) is { } current
            && current.User.AuthEpoch == caller.AuthEpoch && current.User.AuthStamp == caller.AuthStamp ? current : null;

    private async Task<(PasskeyCredentialRecord Record, string Version)?> ReadAsync(string instanceId, string userId, CancellationToken cancellationToken)
    {
        var rowData = await store.GetAsync(AuthStore.PasskeyTable, instanceId, userId, cancellationToken).ConfigureAwait(false);
        if (rowData is null)
        {
            return null;
        }

        try
        {
            var record = rowData.Body.Deserialize(WebAuthnJson.Default.PasskeyCredentialRecord);
            if (record is null || record.InstanceId != instanceId || record.UserId != userId || record.AuthEpoch < 0
                || record.Entries.Count > AuthStore.MaxCredentialBindings
                || record.Entries.Any(entry => string.IsNullOrEmpty(entry.CredentialId) || entry.CredentialId.Length > 1024
                    || entry.PublicKeyCose is not { Length: > 0 and <= 4096 }))
            {
                throw new InvalidDataException("Invalid stored passkey credential.");
            }

            return (record, rowData.Version);
        }
        catch (JsonException error)
        {
            throw ApiException.Unavailable(error);
        }
    }

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
[JsonSerializable(typeof(PasskeyCredentialRecord))]
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

public sealed record PasskeyAssertionRequest(string? Id, string? RawId, string? Type, PasskeyAssertionInnerRequest? Response);

public sealed record PasskeyAssertionInnerRequest(
    [property: JsonPropertyName("clientDataJSON")] string? ClientDataJson,
    [property: JsonPropertyName("authenticatorData")] string? AuthenticatorData,
    string? Signature,
    [property: JsonPropertyName("userHandle")] string? UserHandle = null);

public sealed record PasskeyEnrolledBody(string Id, string? Name, string Aaguid, DateTimeOffset CreatedAt);

public sealed record PasskeyItemBody(string Id, string? Name, string Aaguid, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

public sealed record PasskeyListBody(IReadOnlyList<PasskeyItemBody> Passkeys);

public sealed record PasskeyRenameRequest(string? Name);
