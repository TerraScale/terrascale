using Dapper;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OtpNet;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.Mfa;

public sealed class TotpCredentials(AuthData data, Credentials credentials, Users users, AttemptLimiter limiter, AuthOptions options, TimeProvider clock, ProjectAuthSettings settings, IEmailSender email)
{
    private static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RecoveryLifetime = TimeSpan.FromDays(365);
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private const int RecoveryCount = 8;

    public async Task<bool> AllowsPrimaryAuthenticationAsync(UserRecord caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return await this.PrimaryUserAsync(caller.InstanceId, caller.Id, caller.AuthEpoch.ToString(CultureInfo.InvariantCulture), caller.AuthStamp, caller.MultiFactor ? caller.MfaMethod ?? "totp" : null, cancellationToken)
             is not null;
    }

    public async Task<(bool Enabled, int RecoveryCodes)> StatusAsync(string instanceId, string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await this.ReadAsync(instanceId, userId, cancellationToken) is { Record.Enabled: true } current
                ? (true, current.Record.RecoveryExpiresAt > clock.GetUtcNow() ? current.Record.RecoveryHashes.Count : 0)
                : (false, 0);
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException)
        {
            throw ApiException.Unavailable(error);
        }
    }

    public async Task<string?> ChallengeAsync(UserRecord user, bool enrollment, string? ssoProof, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (await this.CurrentUserAsync(user, cancellationToken) is null
            || enrollment && (!user.InboxProven
                || await this.HasAllowedFactorAsync(user, ssoProof is not null, cancellationToken)))
        {
            return null;
        }

        var challenge = new MfaChallenge(AuthIds.New(), user.Id, user.AuthEpoch, user.AuthStamp, clock.GetUtcNow() + ChallengeLifetime, user.AuthenticationBindings, enrollment ? "enrollment" : "verification", user.InboxProven, ssoProof);
        return SecretBox.Seal(options.MasterKey, JsonSerializer.SerializeToUtf8Bytes(challenge, MfaJson.Default.MfaChallenge), "mfa-challenge:" + user.InstanceId);
    }

    internal async Task<string?> InboxChallengeAsync(UserRecord user, CancellationToken cancellationToken)
    {
        if (await this.CurrentUserAsync(user, cancellationToken) is null
            || await this.HasAllowedFactorAsync(user, false, cancellationToken))
        {
            return null;
        }

        var challenge = new MfaChallenge(AuthIds.New(), user.Id, user.AuthEpoch, user.AuthStamp, clock.GetUtcNow() + ChallengeLifetime,
            user.AuthenticationBindings, "enrollment-inbox", false, null);
        return SecretBox.Seal(options.MasterKey, JsonSerializer.SerializeToUtf8Bytes(challenge, MfaJson.Default.MfaChallenge), "mfa-challenge:" + user.InstanceId);
    }

    public async Task<ChallengedSignIn?> ChallengedAsync(string instanceId, string? sealedChallenge, string purpose, CancellationToken cancellationToken = default)
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

        if (challenge is null || challenge.Purpose != purpose || challenge.ExpiresAt <= clock.GetUtcNow() || !TerraScaleIds.IsValid(challenge.UserId) || !TerraScaleIds.IsValid(challenge.Id)
            || challenge.Bindings.Count >= Credentials.MaxBindings)
        {
            return null;
        }

        var completion = Credentials.New("mfa-challenge", challenge.Id);
        return !await credentials.IsClaimedAsync(instanceId, completion, cancellationToken)
            && await users.GetAsync(instanceId, challenge.UserId, cancellationToken) is { Disabled: false } user
            && user.AuthEpoch == challenge.AuthEpoch && user.AuthStamp == challenge.AuthStamp
                ? new ChallengedSignIn(user with { AuthenticationBindings = [.. challenge.Bindings, completion], InboxProven = challenge.InboxProven }, completion, challenge.ExpiresAt) { SsoProof = challenge.SsoProof }
                : null;
    }

    internal async Task<bool> HasAllowedFactorAsync(UserRecord user, bool viaSso, CancellationToken cancellationToken)
    {
        var policy = Tenancy.IsProject(user.InstanceId)
            ? (await settings.GetAsync(user.InstanceId, cancellationToken)).Configuration ?? ConfigurationBody.Default
            : ConfigurationBody.Default;
        return policy.AllowTotp && (await this.StatusAsync(user.InstanceId, user.Id, cancellationToken)).Enabled
            || (Tenancy.IsProject(user.InstanceId) || viaSso) && policy.AllowPasskey && await this.HasPasskeyAsync(user.InstanceId, user.Id, cancellationToken);
    }

    public Task<bool> CompleteAsync(ChallengedSignIn challenged, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenged);
        return this.VerifyAsync(challenged.User, code, new MfaAuthorization(Credentials.Claim(challenged.User.InstanceId, challenged.Completion), challenged.ExpiresAt), cancellationToken);
    }

    public async Task<UserRecord?> PrimaryUserAsync(string instanceId, string userId, string? authEpoch, string? authStamp, string? multiFactorMethod, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        ArgumentNullException.ThrowIfNull(userId);
        if (authEpoch is null || !TerraScaleIds.IsValid(userId))
        {
            return null;
        }

        try
        {
            var enabled = (await this.ReadAsync(instanceId, userId, cancellationToken))?.Record.Enabled == true;
            var policy = Tenancy.IsProject(instanceId) ? (await settings.GetAsync(instanceId, cancellationToken)).Configuration ?? ConfigurationBody.Default : null;
            var passkey = policy?.AllowPasskey == true && (policy.RequireMfa || multiFactorMethod == "recovery")
                && await this.HasPasskeyAsync(instanceId, userId, cancellationToken);
            var enrolled = policy is null || !policy.RequireMfa || policy.AllowTotp && enabled || passkey;
            var allowed = policy is null || multiFactorMethod is null || multiFactorMethod == "totp" && policy.AllowTotp
                || multiFactorMethod == "passkey" && policy.AllowPasskey || multiFactorMethod == "recovery" && enabled && (policy.AllowTotp || passkey);
            return enrolled && allowed && (!(policy?.RequireMfa == true || enabled && policy?.AllowTotp != false) || multiFactorMethod is not null)
                && await users.GetAsync(instanceId, userId, cancellationToken) is { Disabled: false } user
                && user.AuthEpoch.ToString(CultureInfo.InvariantCulture) == authEpoch && user.AuthStamp == authStamp
                    ? user with { MultiFactor = multiFactorMethod is not null, MfaMethod = multiFactorMethod }
                    : null;
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException)
        {
            throw ApiException.Unavailable(error);
        }
    }

    public Task<TotpEnrollment?> BeginAsync(UserRecord caller, CancellationToken cancellationToken = default) =>
        this.BeginAsync(caller, null, cancellationToken);

    internal async Task<TotpEnrollment?> BeginAsync(UserRecord caller, MfaAuthorization? authorization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await this.CurrentUserAsync(caller, cancellationToken) is not { } user)
        {
            return null;
        }

        var current = await this.ReadAsync(caller, cancellationToken);
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
        if (!await this.CommitAsync(Change.Assert(user), pending, current?.Version, authorization, cancellationToken))
        {
            return null;
        }

        return new TotpEnrollment(encoded, new OtpUri(OtpType.Totp, encoded, user.Email, "TerraScale").ToString(), expires);
    }

    public Task<IssuedRecoveryCodes?> ConfirmAsync(UserRecord caller, string code, CancellationToken cancellationToken = default) =>
        this.ConfirmAsync(caller, code, null, cancellationToken);

    internal async Task<IssuedRecoveryCodes?> ConfirmAsync(UserRecord caller, string code, MfaAuthorization? authorization, CancellationToken cancellationToken = default)
    {
        var issued = await this.ActivateAsync(caller, code, authorization, cancellationToken);
        if (issued is not null)
        {
            await FactorEnrollmentNotice.SendAsync(email, issued.User, cancellationToken);
        }

        return issued;
    }

    internal async Task<IssuedRecoveryCodes?> ActivateAsync(UserRecord caller, string code, MfaAuthorization? authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await this.LimitAsync(caller, cancellationToken);
        if (await this.CurrentUserAsync(caller, cancellationToken) is not { } user
            || await this.ReadAsync(caller, cancellationToken) is not { } current
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
        var advanced = Users.BindAuthenticationState(user with { AuthEpoch = checked(user.AuthEpoch + 1), AuthStamp = AuthIds.New() });
        var update = Change.Update(advanced);
        if (!await this.CommitAsync(update, active, current.Version, authorization, cancellationToken))
        {
            return null;
        }

        return new IssuedRecoveryCodes(advanced with { Version = update.Version }, codes, recoveryExpires);
    }

    public Task<bool> VerifyAsync(UserRecord caller, string code, CancellationToken cancellationToken = default) =>
        this.VerifyAsync(caller, code, null, cancellationToken);

    internal async Task<bool> VerifyAsync(UserRecord caller, string code, MfaAuthorization? authorization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await this.LimitAsync(caller, cancellationToken);
        if (await this.CurrentUserAsync(caller, cancellationToken) is not { } user
            || await this.ReadAsync(caller, cancellationToken) is not { } current
            || this.Consume(caller, current.Record, code) is not { } proof)
        {
            return false;
        }

        return await this.CommitAsync(Change.Assert(user), proof, current.Version, authorization, cancellationToken);
    }

    public Task<bool> DisableAsync(UserRecord caller, string code, CancellationToken cancellationToken = default) =>
        this.DisableAsync(caller, code, null, cancellationToken);

    internal async Task<bool> DisableAsync(UserRecord caller, string code, MfaAuthorization? authorization, CancellationToken cancellationToken = default) =>
        await this.DisabledUserAsync(caller, code, authorization, cancellationToken) is not null;

    internal async Task<UserRecord?> DisabledUserAsync(UserRecord caller, string code, MfaAuthorization? authorization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await this.LimitAsync(caller, cancellationToken);
        if (await this.CurrentUserAsync(caller, cancellationToken) is not { } user
            || await this.ReadAsync(caller, cancellationToken) is not { } current
            || this.Consume(caller, current.Record, code) is null)
        {
            return null;
        }

        var disabled = current.Record with { SecretBox = null, Enabled = false, EnrollmentExpiresAt = null, LastAcceptedStep = -1, RecoveryHashes = [], RecoveryExpiresAt = null };
        var advanced = Users.BindAuthenticationState(user with { AuthEpoch = checked(user.AuthEpoch + 1), AuthStamp = AuthIds.New() });
        var update = Change.Update(advanced);
        return await this.CommitAsync(update, disabled, current.Version, authorization, cancellationToken)
            ? advanced with { Version = update.Version }
            : null;
    }

    internal async Task<IssuedRecoveryCodes?> RegenerateAsync(UserRecord caller, string code, MfaAuthorization? authorization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await this.LimitAsync(caller, cancellationToken);
        if (await this.CurrentUserAsync(caller, cancellationToken) is not { } user
            || await this.ReadAsync(caller, cancellationToken) is not { } current
            || this.Consume(caller, current.Record, code) is not { } proof)
        {
            return null;
        }

        var codes = Enumerable.Range(0, RecoveryCount).Select(_ => Tokens.RandomToken(24)).ToArray();
        var recoveryExpires = clock.GetUtcNow() + RecoveryLifetime;
        var regenerated = proof with { RecoveryHashes = codes.Select(Tokens.Sha256Hex).ToArray(), RecoveryExpiresAt = recoveryExpires };
        return await this.CommitAsync(Change.Assert(user), regenerated, current.Version, authorization, cancellationToken)
            ? new IssuedRecoveryCodes(user, codes, recoveryExpires)
            : null;
    }

    private async Task<UserRecord?> CurrentUserAsync(UserRecord caller, CancellationToken cancellationToken) =>
        await users.GetAsync(caller.InstanceId, caller.Id, cancellationToken) is { } current
        && current.AuthEpoch == caller.AuthEpoch && current.AuthStamp == caller.AuthStamp ? current : null;

    private Task<(TotpCredentialRecord Record, string Version)?> ReadAsync(UserRecord caller, CancellationToken cancellationToken) =>
        this.ReadAsync(caller.InstanceId, caller.Id, cancellationToken);

    private Task<bool> HasPasskeyAsync(string instanceId, string userId, CancellationToken cancellationToken) =>
        data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM passkeys WHERE instance_id = @instanceId AND user_id = @userId)", new { instanceId, userId }), cancellationToken);

    private async Task<(TotpCredentialRecord Record, string Version)?> ReadAsync(string instanceId, string userId, CancellationToken cancellationToken)
    {
        var rowData = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<TotpCredentialRecord>(
            "SELECT * FROM mfa_credentials WHERE user_id = @userId", new { userId }), cancellationToken));
        if (rowData is null)
        {
            return null;
        }

        try
        {
            var record = rowData.Value;
            if (record.InstanceId != instanceId || record.UserId != userId || record.EnrollmentEpoch < 0
                || record.LastAcceptedStep < -1 || record.RecoveryHashes is null || record.RecoveryHashes.Count > RecoveryCount
                || record.RecoveryHashes.Any(hash => hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigitLower))
                || record.RecoveryHashes.Distinct(StringComparer.Ordinal).Count() != record.RecoveryHashes.Count
                || record.Enabled && (record.SecretBox is null || record.EnrollmentExpiresAt is not null || record.LastAcceptedStep < 0 || record.RecoveryExpiresAt is null)
                || !record.Enabled && (record.LastAcceptedStep != -1 || record.RecoveryHashes.Count != 0 || record.RecoveryExpiresAt is not null
                    || (record.SecretBox is null) != (record.EnrollmentExpiresAt is null)))
            {
                throw new InvalidDataException("Invalid stored MFA credential.");
            }

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

            return (record, rowData.Version);
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException)
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

    private async Task<bool> CommitAsync(Change user, TotpCredentialRecord record, string? version, MfaAuthorization? authorization, CancellationToken cancellationToken)
    {
        if (authorization is not null && authorization.ExpiresAt <= clock.GetUtcNow())
        {
            return false;
        }

        List<Change> changes =
        [
            user,
            version is null ? Change.Insert(record) : Change.Update(record, version),
        ];
        if (authorization is not null)
        {
            changes.Add(authorization.Operation);
        }

        return await data.SaveAsync(changes, cancellationToken);
    }

    private async Task LimitAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        if (await limiter.AcquireAsync(caller.InstanceId, "mfa-proof", caller.Id, 10, AttemptWindow, cancellationToken) is { } wait)
        {
            throw ApiException.RateLimited("Too many MFA attempts; retry after the Retry-After delay.", wait);
        }
    }

    private static string Context(UserRecord user) => Context(user.InstanceId, user.Id);

    private static string Context(string instanceId, string userId) => "totp:" + AuthIds.Derived(instanceId, "mfa_credentials", userId);
}

public sealed record ChallengedSignIn(UserRecord User, CredentialBinding Completion, DateTimeOffset ExpiresAt)
{
    internal string? SsoProof { get; init; }
}

internal sealed record MfaChallenge(string Id, string UserId, long AuthEpoch, string? AuthStamp, DateTimeOffset ExpiresAt, IReadOnlyList<CredentialBinding> Bindings, string Purpose, bool InboxProven,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SsoProof = null);

internal sealed record MfaAuthorization(Change Operation, DateTimeOffset ExpiresAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(MfaChallenge))]
internal sealed partial class MfaJson : JsonSerializerContext;
