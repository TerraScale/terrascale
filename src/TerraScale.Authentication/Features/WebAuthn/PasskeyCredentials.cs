using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Fido2NetLib;
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
    public const string ChallengeKind = "passkey-challenge";

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
            || !await store.AreCredentialsCurrentAsync(caller.InstanceId, caller.AuthenticationBindings, cancellationToken).ConfigureAwait(false)
            || await store.FindUniqueAsync(caller.InstanceId, ChallengeKind, challenge.Id, cancellationToken).ConfigureAwait(false) is not null)
        {
            return null;
        }

        if (response.RawId is not { Length: > 0 and <= 1024 } rawId)
        {
            return null;
        }

        var credentialId = Base64UrlEncode(rawId);
        if (await store.FindUniqueOwnerAsync(caller.InstanceId, CredentialKind, credentialId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw Taken();
        }

        Fido2NetLib.Objects.RegisteredPublicKeyCredential registered;
        try
        {
            registered = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = CredentialCreateOptions.FromJson(challenge.OptionsJson),
                IsCredentialIdUniqueToUserCallback = async (parameters, token) =>
                    await store.FindUniqueOwnerAsync(caller.InstanceId, CredentialKind, Base64UrlEncode(parameters.CredentialId), token).ConfigureAwait(false) is not { } holder
                    || holder == caller.Id,
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
            IssuanceBindings = [.. caller.AuthenticationBindings, AuthStore.NewCredentialBinding(CredentialKind, credentialId)],
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
            AuthStore.Claim(user.User.InstanceId, CredentialKind, credentialId, user.User.Id),
            AuthStore.Claim(user.User.InstanceId, ChallengeKind, challenge.Id, user.User.Id),
        };
        if (await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw Taken();
        }

        return record.Entries[^1];
    }

    public static ApiException Taken() => new(409, "passkey_taken", "That passkey is already enrolled.");

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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PasskeyCredentialRecord))]
[JsonSerializable(typeof(PasskeyChallenge))]
[JsonSerializable(typeof(PasskeyOptionsRequest))]
[JsonSerializable(typeof(PasskeyOptionsBody))]
[JsonSerializable(typeof(PasskeyVerifyRequest))]
[JsonSerializable(typeof(PasskeyEnrolledBody))]
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

public sealed record PasskeyEnrolledBody(string Id, string? Name, string Aaguid, DateTimeOffset CreatedAt);
