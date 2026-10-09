using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OtpNet;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.RateLimiting;
using TerraScale.Auth.Security;
using TerraScale.Auth.Store;

namespace TerraScale.Auth.Features.Mfa;

/// <summary>
/// Internal TOTP and recovery credentials. Callers must authenticate and reauthenticate before enrollment or disabling.
/// No HTTP flow exposes this service until sign-in enforcement lands. Every proof and credential change is conditional
/// on both the credential version and the user's authentication epoch in the same TerraBase transaction.
/// Proof expiry is checked before the transaction; an acknowledged commit always reports its successful outcome.
/// </summary>
public sealed class TotpCredentials(AuthStore store, Users users, AttemptLimiter limiter, AuthOptions options, TimeProvider clock)
{
    private static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RecoveryLifetime = TimeSpan.FromDays(365);
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(15);
    private const int RecoveryCount = 8;

    /// <summary>Begins or supersedes inactive enrollment, or null for an active factor, stale caller or lost race.</summary>
    public async Task<TotpEnrollment?> BeginAsync(UserRecord caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await this.CurrentUserAsync(caller, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            return null;
        }

        var current = await this.ReadAsync(caller, cancellationToken).ConfigureAwait(false);
        if (current?.Record.Enabled == true)
        {
            return null;
        }

        var secret = RandomNumberGenerator.GetBytes(20);
        string encoded;
        string boxed;
        try
        {
            encoded = Base32Encoding.ToString(secret);
            boxed = SecretBox.Seal(options.MasterKey, secret, Context(caller));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        var expires = clock.GetUtcNow() + EnrollmentLifetime;
        var pending = new TotpCredentialRecord(caller.InstanceId, caller.Id, boxed, false, caller.AuthEpoch, expires, -1, [], null);
        if (!await this.CommitAsync(user.User, pending, current?.Version, null, cancellationToken).ConfigureAwait(false) || expires <= clock.GetUtcNow())
        {
            return null;
        }

        return new TotpEnrollment(encoded, new OtpUri(OtpType.Totp, encoded, user.User.Email, "TerraScale").ToString(), expires);
    }

    /// <summary>Confirms enrollment and atomically advances the user epoch; returns recovery codes once.</summary>
    public async Task<IssuedRecoveryCodes?> ConfirmAsync(UserRecord caller, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await this.LimitAsync(caller, cancellationToken).ConfigureAwait(false);
        if (await this.CurrentUserAsync(caller, cancellationToken).ConfigureAwait(false) is not { } user
            || await this.ReadAsync(caller, cancellationToken).ConfigureAwait(false) is not { } current
            || current.Record.Enabled || current.Record.SecretBox is null || current.Record.EnrollmentEpoch != caller.AuthEpoch
            || current.Record.EnrollmentExpiresAt is not { } expires || expires <= clock.GetUtcNow()
            || this.Step(caller, current.Record, code) is not { } step)
        {
            return null;
        }

        var codes = Enumerable.Range(0, RecoveryCount).Select(_ => Tokens.RandomToken(24)).ToArray();
        var recoveryExpires = clock.GetUtcNow() + RecoveryLifetime;
        var active = current.Record with
        {
            Enabled = true,
            EnrollmentExpiresAt = null,
            LastAcceptedStep = step,
            RecoveryHashes = codes.Select(Tokens.Sha256Hex).ToArray(),
            RecoveryExpiresAt = recoveryExpires,
        };
        var advanced = user.User with { AuthEpoch = checked(user.User.AuthEpoch + 1) };
        return await this.CommitAsync(user.User, active, current.Version, (advanced, user.Version), cancellationToken).ConfigureAwait(false)
            ? new IssuedRecoveryCodes(advanced, codes, recoveryExpires)
            : null;
    }

    /// <summary>Consumes an active TOTP step or recovery code exactly once; a stale caller never receives a proof.</summary>
    public async Task<bool> VerifyAsync(UserRecord caller, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await this.LimitAsync(caller, cancellationToken).ConfigureAwait(false);
        if (await this.CurrentUserAsync(caller, cancellationToken).ConfigureAwait(false) is not { } user
            || await this.ReadAsync(caller, cancellationToken).ConfigureAwait(false) is not { } current
            || this.Consume(caller, current.Record, code) is not { } proof)
        {
            return false;
        }

        return await this.CommitAsync(user.User, proof, current.Version, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Consumes a proof, disables the factor and recovery codes, and advances the epoch atomically.</summary>
    public async Task<bool> DisableAsync(UserRecord caller, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await this.LimitAsync(caller, cancellationToken).ConfigureAwait(false);
        if (await this.CurrentUserAsync(caller, cancellationToken).ConfigureAwait(false) is not { } user
            || await this.ReadAsync(caller, cancellationToken).ConfigureAwait(false) is not { } current
            || this.Consume(caller, current.Record, code) is null)
        {
            return false;
        }

        var disabled = current.Record with { SecretBox = null, Enabled = false, EnrollmentExpiresAt = null, LastAcceptedStep = -1, RecoveryHashes = [], RecoveryExpiresAt = null };
        var advanced = user.User with { AuthEpoch = checked(user.User.AuthEpoch + 1) };
        return await this.CommitAsync(user.User, disabled, current.Version, (advanced, user.Version), cancellationToken).ConfigureAwait(false);
    }

    private async Task<(UserRecord User, string Version)?> CurrentUserAsync(UserRecord caller, CancellationToken cancellationToken) =>
        await users.GetVersionedAsync(caller.InstanceId, caller.Id, cancellationToken).ConfigureAwait(false) is { } current
        && current.User.AuthEpoch == caller.AuthEpoch ? current : null;

    private async Task<(TotpCredentialRecord Record, string Version)?> ReadAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        try
        {
            var record = document.Document.Deserialize(MfaJson.Default.TotpCredentialRecord);
            if (record is null || record.InstanceId != caller.InstanceId || record.UserId != caller.Id || record.EnrollmentEpoch < 0
                || record.LastAcceptedStep < -1 || record.RecoveryHashes is null || record.RecoveryHashes.Count > RecoveryCount
                || record.RecoveryHashes.Any(hash => hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigitLower))
                || record.RecoveryHashes.Distinct(StringComparer.Ordinal).Count() != record.RecoveryHashes.Count
                || record.Enabled && (record.SecretBox is null || record.EnrollmentExpiresAt is not null || record.LastAcceptedStep < 0 || record.RecoveryExpiresAt is null)
                || !record.Enabled && (record.LastAcceptedStep != -1 || record.RecoveryHashes.Count != 0 || record.RecoveryExpiresAt is not null
                    || (record.SecretBox is null) != (record.EnrollmentExpiresAt is null)))
            {
                throw new InvalidDataException("Invalid stored MFA credential.");
            }

            // Validate ciphertext even for recovery proofs: corrupt factor state never behaves as a disabled factor.
            if (record.SecretBox is not null)
            {
                var secret = SecretBox.Open(options.MasterKey, record.SecretBox, Context(caller));
                try
                {
                    if (secret.Length != 20)
                    {
                        throw new InvalidDataException("Invalid stored MFA secret.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(secret);
                }
            }

            return (record, document.Version);
        }
        catch (Exception error) when (error is JsonException or CryptographicException or InvalidDataException)
        {
            throw ApiException.Unavailable(error);
        }
    }

    private TotpCredentialRecord? Consume(UserRecord caller, TotpCredentialRecord record, string code)
    {
        if (!record.Enabled)
        {
            return null;
        }

        if (this.Step(caller, record, code) is { } step)
        {
            return record with { LastAcceptedStep = step };
        }

        if (code is not { Length: 24 } || !code.All(char.IsAsciiLetterOrDigit) || record.RecoveryExpiresAt <= clock.GetUtcNow())
        {
            return null;
        }

        var digest = Tokens.Sha256Hex(code);
        return record.RecoveryHashes.Any(hash => Tokens.FixedTimeEquals(hash, digest))
            ? record with { RecoveryHashes = record.RecoveryHashes.Where(hash => !Tokens.FixedTimeEquals(hash, digest)).ToArray() }
            : null;
    }

    private long? Step(UserRecord caller, TotpCredentialRecord record, string code)
    {
        if (code is not { Length: 6 } || !code.All(char.IsAsciiDigit) || record.SecretBox is null)
        {
            return null;
        }

        var secret = SecretBox.Open(options.MasterKey, record.SecretBox, Context(caller));
        try
        {
            return new Totp(secret, step: 30, mode: OtpHashMode.Sha1, totpSize: 6)
                .VerifyTotp(clock.GetUtcNow().UtcDateTime, code, out var step, new VerificationWindow(previous: 1, future: 1))
                && step > record.LastAcceptedStep ? step : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private async Task<bool> CommitAsync(UserRecord user, TotpCredentialRecord record, string? version,
        (UserRecord User, string Version)? advanced, CancellationToken cancellationToken)
    {
        var serialized = JsonSerializer.SerializeToElement(record, MfaJson.Default.TotpCredentialRecord);
        var credential = version is null ? AuthStore.Create(AuthStore.MfaCollection, user.InstanceId, user.Id, serialized)
            : AuthStore.Replace(AuthStore.MfaCollection, user.InstanceId, user.Id, serialized, version);
        var epoch = new JsonObject
        {
            ["and"] = new JsonArray(
                new JsonObject { ["eq"] = new JsonArray("/instanceId", user.InstanceId) },
                new JsonObject { ["eq"] = new JsonArray("/id", user.Id) },
                new JsonObject { ["eq"] = new JsonArray("/authEpoch", user.AuthEpoch) }),
        };
        using var filter = JsonDocument.Parse(epoch.ToJsonString());
        var account = advanced is { } change
            ? AuthStore.Replace(AuthStore.UserCollection, user.InstanceId, user.Id, JsonSerializer.SerializeToElement(change.User, IdentityJson.Default.UserRecord), change.Version)
            : AuthStore.Require(AuthStore.UserCollection, user.InstanceId, user.Id, filter.RootElement);
        return await store.TransactAsync([account, credential], cancellationToken).ConfigureAwait(false) is null;
    }

    private async Task LimitAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        if (!await limiter.TryAcquireAsync(caller.InstanceId, "mfa-proof", caller.Id, 10, AttemptWindow, cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(429, "rate_limited", "Too many MFA attempts; retry after fifteen minutes.");
        }
    }

    private static string Context(UserRecord user) => "totp:" + AuthStore.NaturalKeyId(user.InstanceId, AuthStore.MfaCollection, user.Id);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(TotpCredentialRecord))]
internal sealed partial class MfaJson : JsonSerializerContext;
