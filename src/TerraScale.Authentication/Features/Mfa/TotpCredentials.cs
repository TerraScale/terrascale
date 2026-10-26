using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OtpNet;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.Mfa;

/// <summary>
/// TOTP and recovery credentials. Callers must authenticate and reauthenticate before enrollment or disabling; the hosted
/// security pages (<c>/ui/{slug}/security</c>) do both. Every proof and credential change is conditional
/// on both the credential version and the user's authentication epoch in the same TerraBase transaction.
/// Proof expiry is checked before the transaction; an acknowledged commit always reports its successful outcome.
/// </summary>
public sealed class TotpCredentials(AuthStore store, Users users, AttemptLimiter limiter, AuthOptions options, TimeProvider clock)
{
    private static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RecoveryLifetime = TimeSpan.FromDays(365);
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private const int RecoveryCount = 8;

    /// <summary>
    /// Whether the current account can authenticate with only its primary credential: never with an active factor.
    /// Invalid or unavailable factor state fails closed.
    /// </summary>
    public async Task<bool> AllowsPrimaryAuthenticationAsync(UserRecord caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return await this.PrimaryUserAsync(caller.InstanceId, caller.Id, caller.AuthEpoch.ToString(CultureInfo.InvariantCulture), caller.AuthStamp, false, cancellationToken)
            .ConfigureAwait(false) is not null;
    }

    /// <summary>Whether the account has an active factor, and how many recovery codes remain; corrupt state fails closed with 503.</summary>
    public async Task<(bool Enabled, int RecoveryCodes)> StatusAsync(string instanceId, string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await this.ReadAsync(instanceId, userId, cancellationToken).ConfigureAwait(false) is { Record.Enabled: true } current
                ? (true, current.Record.RecoveryExpiresAt > clock.GetUtcNow() ? current.Record.RecoveryHashes.Count : 0)
                : (false, 0);
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException)
        {
            throw ApiException.Unavailable(error);
        }
    }

    /// <summary>
    /// A sealed, five-minute second-factor challenge for a user whose primary credential was just verified. It names the
    /// user's authentication state and proof lineage, so a sign-out everywhere, reset or factor change voids it; each
    /// attempt still needs an unused TOTP step or recovery code and charges the MFA attempt budget. It completes once
    /// (<see cref="CompleteAsync"/>). Null when the user's authentication state changed since <paramref name="user"/> was read.
    /// </summary>
    public async Task<string?> ChallengeAsync(UserRecord user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (await this.CurrentUserAsync(user, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var challenge = new MfaChallenge(AuthStore.NewId(), user.Id, user.AuthEpoch, user.AuthStamp, clock.GetUtcNow() + ChallengeLifetime, user.AuthenticationBindings);
        return SecretBox.Seal(options.MasterKey, JsonSerializer.SerializeToUtf8Bytes(challenge, MfaJson.Default.MfaChallenge), "mfa-challenge:" + user.InstanceId);
    }

    /// <summary>
    /// The live, not yet completed <see cref="ChallengeAsync"/> challenge, or null for anything else. Its user carries the
    /// challenge's proof lineage plus the slot its completion claims, so a session opened from it needs that completion to
    /// be the replicated winner. Reads only: it spends no attempt and no proof.
    /// </summary>
    public async Task<ChallengedSignIn?> ChallengedAsync(string instanceId, string? sealedChallenge, CancellationToken cancellationToken = default)
    {
        MfaChallenge? challenge;
        try
        {
            challenge = sealedChallenge is { Length: > 0 and <= 4096 }
                ? JsonSerializer.Deserialize(SecretBox.Open(options.MasterKey, sealedChallenge, "mfa-challenge:" + instanceId), MfaJson.Default.MfaChallenge)
                : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        {
            return null;
        }

        if (challenge is null || challenge.ExpiresAt <= clock.GetUtcNow() || !TerraScaleIds.IsValid(challenge.UserId) || !TerraScaleIds.IsValid(challenge.Id)
            || challenge.Bindings.Count >= AuthStore.MaxCredentialBindings)
        {
            return null;
        }

        var completion = AuthStore.NewCredentialBinding("mfa-challenge", challenge.Id);
        // A completed challenge fails fast here; the claim in the completing transaction is the authority.
        return await store.FindUniqueAsync(instanceId, completion.Kind, completion.Key, cancellationToken).ConfigureAwait(false) is null
            && await users.GetAsync(instanceId, challenge.UserId, cancellationToken).ConfigureAwait(false) is { Disabled: false } user
            && user.AuthEpoch == challenge.AuthEpoch && user.AuthStamp == challenge.AuthStamp
                ? new ChallengedSignIn(user with { AuthenticationBindings = [.. challenge.Bindings, completion] }, completion, challenge.ExpiresAt)
                : null;
    }

    /// <summary>
    /// Consumes an unused TOTP step or recovery code and completes <paramref name="challenged"/> in one transaction: of any
    /// number of attempts on one challenge against the same regional store, at most one commits. Concurrent regional
    /// winners reconcile through the completion lineage; this is not global linearizability. A refused commit spends no proof.
    /// </summary>
    public Task<bool> CompleteAsync(ChallengedSignIn challenged, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenged);
        return this.VerifyAsync(challenged.User, code, new MfaAuthorization(AuthStore.CredentialClaim(challenged.User.InstanceId, challenged.Completion), challenged.ExpiresAt), cancellationToken);
    }

    /// <summary>
    /// The user as TerraBase holds it now, when it still has the authentication epoch <paramref name="authEpoch"/> and
    /// state <paramref name="authStamp"/> (a token's claims) and either has no active factor or <paramref name="multiFactor"/>
    /// says the token's sign-in proved it; otherwise null. The result's <see cref="UserRecord.MultiFactor"/> carries that proof. The user is read after its factor, never before or alongside it, so an activation or a sign-out
    /// everywhere that lands during the factor read is seen. One user read replaces a caller's own read before this
    /// check. Invalid or unavailable factor state fails closed with 503.
    /// </summary>
    public async Task<UserRecord?> PrimaryUserAsync(string instanceId, string userId, string? authEpoch, string? authStamp, bool multiFactor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        ArgumentNullException.ThrowIfNull(userId);
        if (authEpoch is null || !TerraScaleIds.IsValid(userId))
        {
            return null;
        }

        try
        {
            var enabled = (await this.ReadAsync(instanceId, userId, cancellationToken).ConfigureAwait(false))?.Record.Enabled == true;
            return (!enabled || multiFactor)
                && await users.GetVersionedAsync(instanceId, userId, cancellationToken).ConfigureAwait(false) is { User: var user }
                && user.AuthEpoch.ToString(CultureInfo.InvariantCulture) == authEpoch && user.AuthStamp == authStamp
                    ? user with { MultiFactor = enabled }
                    : null;
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException)
        {
            throw ApiException.Unavailable(error);
        }
    }

    /// <summary>Begins or supersedes inactive enrollment, or null for an active factor, stale caller or lost race.</summary>
    public Task<TotpEnrollment?> BeginAsync(UserRecord caller, CancellationToken cancellationToken = default) =>
        this.BeginAsync(caller, null, cancellationToken);

    /// <summary>Includes the caller's validated authorization transition in the credential transaction.</summary>
    internal async Task<TotpEnrollment?> BeginAsync(UserRecord caller, MfaAuthorization? authorization, CancellationToken cancellationToken = default)
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
        if (authorization is not null && authorization.ExpiresAt < expires)
        {
            expires = authorization.ExpiresAt;
        }

        var pending = new TotpCredentialRecord(caller.InstanceId, caller.Id, boxed, false, caller.AuthEpoch, expires, -1, [], null);
        if (!await this.CommitAsync(user.User, pending, current?.Version, null, authorization, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TotpEnrollment(encoded, new OtpUri(OtpType.Totp, encoded, user.User.Email, "TerraScale").ToString(), expires);
    }

    /// <summary>Confirms enrollment and atomically advances the user epoch; returns recovery codes once.</summary>
    public Task<IssuedRecoveryCodes?> ConfirmAsync(UserRecord caller, string code, CancellationToken cancellationToken = default) =>
        this.ConfirmAsync(caller, code, null, cancellationToken);

    /// <summary>Consumes the caller's validated authorization atomically with activation and epoch advancement.</summary>
    internal async Task<IssuedRecoveryCodes?> ConfirmAsync(UserRecord caller, string code, MfaAuthorization? authorization, CancellationToken cancellationToken = default)
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
        var advanced = Users.BindAuthenticationState(user.User with { AuthEpoch = checked(user.User.AuthEpoch + 1), AuthStamp = AuthStore.NewId() });
        return await this.CommitAsync(user.User, active, current.Version, (advanced, user.Version), authorization, cancellationToken).ConfigureAwait(false)
            ? new IssuedRecoveryCodes(advanced, codes, recoveryExpires)
            : null;
    }

    /// <summary>Consumes an active TOTP step or recovery code exactly once; a stale caller never receives a proof.</summary>
    public Task<bool> VerifyAsync(UserRecord caller, string code, CancellationToken cancellationToken = default) =>
        this.VerifyAsync(caller, code, null, cancellationToken);

    /// <summary>Consumes the caller's validated authorization and the factor proof in one transaction.</summary>
    internal async Task<bool> VerifyAsync(UserRecord caller, string code, MfaAuthorization? authorization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await this.LimitAsync(caller, cancellationToken).ConfigureAwait(false);
        if (await this.CurrentUserAsync(caller, cancellationToken).ConfigureAwait(false) is not { } user
            || await this.ReadAsync(caller, cancellationToken).ConfigureAwait(false) is not { } current
            || this.Consume(caller, current.Record, code) is not { } proof)
        {
            return false;
        }

        return await this.CommitAsync(user.User, proof, current.Version, null, authorization, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Consumes a proof, disables the factor and recovery codes, and advances the epoch atomically.</summary>
    public Task<bool> DisableAsync(UserRecord caller, string code, CancellationToken cancellationToken = default) =>
        this.DisableAsync(caller, code, null, cancellationToken);

    /// <summary>Consumes the caller's validated authorization atomically with disabling and epoch advancement.</summary>
    internal async Task<bool> DisableAsync(UserRecord caller, string code, MfaAuthorization? authorization, CancellationToken cancellationToken = default)
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
        var advanced = Users.BindAuthenticationState(user.User with { AuthEpoch = checked(user.User.AuthEpoch + 1), AuthStamp = AuthStore.NewId() });
        return await this.CommitAsync(user.User, disabled, current.Version, (advanced, user.Version), authorization, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(UserRecord User, string Version)?> CurrentUserAsync(UserRecord caller, CancellationToken cancellationToken) =>
        await users.GetVersionedAsync(caller.InstanceId, caller.Id, cancellationToken).ConfigureAwait(false) is { } current
        && current.User.AuthEpoch == caller.AuthEpoch && current.User.AuthStamp == caller.AuthStamp ? current : null;

    private Task<(TotpCredentialRecord Record, string Version)?> ReadAsync(UserRecord caller, CancellationToken cancellationToken) =>
        this.ReadAsync(caller.InstanceId, caller.Id, cancellationToken);

    private async Task<(TotpCredentialRecord Record, string Version)?> ReadAsync(string instanceId, string userId, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(AuthStore.MfaCollection, instanceId, userId, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        try
        {
            var record = document.Document.Deserialize(MfaJson.Default.TotpCredentialRecord);
            if (record is null || record.InstanceId != instanceId || record.UserId != userId || record.EnrollmentEpoch < 0
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
                var secret = SecretBox.Open(options.MasterKey, record.SecretBox, Context(instanceId, userId));
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
        (UserRecord User, string Version)? advanced, MfaAuthorization? authorization, CancellationToken cancellationToken)
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
        if (authorization is not null && authorization.ExpiresAt <= clock.GetUtcNow())
        {
            return false;
        }

        return await store.TransactAsync(authorization is null ? [account, credential] : [account, credential, authorization.Operation], cancellationToken).ConfigureAwait(false) is null;
    }

    private async Task LimitAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        if (await limiter.AcquireAsync(caller.InstanceId, "mfa-proof", caller.Id, 10, AttemptWindow, cancellationToken).ConfigureAwait(false) is { } wait)
        {
            throw ApiException.RateLimited("Too many MFA attempts; retry after the Retry-After delay.", wait);
        }
    }

    private static string Context(UserRecord user) => Context(user.InstanceId, user.Id);

    private static string Context(string instanceId, string userId) => "totp:" + AuthStore.NaturalKeyId(instanceId, AuthStore.MfaCollection, userId);
}

/// <summary>A live sign-in challenge: its user with the proof lineage a completed session carries, and its one-use slot.</summary>
public sealed record ChallengedSignIn(UserRecord User, CredentialBinding Completion, DateTimeOffset ExpiresAt);

/// <summary>The sealed contents of a sign-in's second-factor challenge.</summary>
internal sealed record MfaChallenge(string Id, string UserId, long AuthEpoch, string? AuthStamp, DateTimeOffset ExpiresAt, IReadOnlyList<CredentialBinding> Bindings);

/// <summary>A trusted caller's validated, expiring authorization transition, committed with the MFA change.</summary>
internal sealed record MfaAuthorization(StoreOperation Operation, DateTimeOffset ExpiresAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(TotpCredentialRecord))]
[JsonSerializable(typeof(MfaChallenge))]
internal sealed partial class MfaJson : JsonSerializerContext;
