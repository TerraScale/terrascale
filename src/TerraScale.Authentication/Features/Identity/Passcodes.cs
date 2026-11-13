using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Identity;

public enum PasscodePurpose
{
    SignIn,

    PasswordReset,

    SignUp,

    StepUp,

    EnrollmentInbox,
}

public sealed record IssuedPasscode(string Email, string Code, DateTimeOffset ExpiresAt, PasscodePurpose Purpose = PasscodePurpose.SignIn)
{
    public string Tenant { get; init; } = "platform";

    public string Key { get; init; } = "";

    public string IssueId { get; init; } = "";

    public override string ToString() => nameof(IssuedPasscode);
}

public sealed class Passcodes(AuthData data, Credentials credentials, Users users, Sessions sessions, WebhookOutbox outbox, TimeProvider clock, AuthOptions options, SignUpRestrictions restrictions)
{
    public const int MaxAttempts = 5;

    public const int MaxIssuesPerWindow = 5;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan IssueWindow = TimeSpan.FromHours(1);

    internal const string SignUpKey = "sign-up:";

    private readonly byte[] digestKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth passcode digests"u8.ToArray());

    public Task<IssuedPasscode?> RequestAsync(string instanceId, string email, CancellationToken cancellationToken = default) =>
        this.RequestAsync(instanceId, email, PasscodePurpose.SignIn, cancellationToken);

    internal Task<IssuedPasscode?> RequestStepUpAsync(UserRecord user, string requestId, CancellationToken cancellationToken) =>
        this.IssueAsync(user.InstanceId, requestId, user.Id, user.Email, user.AuthEpoch, user.AuthStamp, PasscodePurpose.StepUp,
            null, Tokens.Sha256Hex(requestId), cancellationToken);

    internal async Task<UserRecord?> RedeemStepUpAsync(UserRecord user, string requestId, string code, CancellationToken cancellationToken)
    {
        if (!WellFormed(code))
        {
            return null;
        }

        var consumed = await this.ConsumeAsync(user.InstanceId, requestId, code.Trim(), PasscodePurpose.StepUp,
            value => value.UserId == user.Id && value.AuthEpoch == user.AuthEpoch && value.AuthStamp == user.AuthStamp
                && value.PendingTokenHash == Tokens.Sha256Hex(requestId), cancellationToken).ConfigureAwait(false);
        return consumed?.AuthenticatedUser ?? (consumed is { Binding: { } binding }
            ? user with { AuthenticationBindings = [.. user.AuthenticationBindings, .. consumed.IssuanceBindings, binding] } : null);
    }

    internal Task<IssuedPasscode?> RequestEnrollmentInboxAsync(UserRecord user, string challenge, CancellationToken cancellationToken) =>
        this.IssueAsync(user.InstanceId, "enrollment-inbox:" + Tokens.Sha256Hex(challenge), user.Id, user.Email, user.AuthEpoch, user.AuthStamp,
            PasscodePurpose.EnrollmentInbox, null, Tokens.Sha256Hex(challenge), cancellationToken);

    internal async Task<UserRecord?> RedeemEnrollmentInboxAsync(UserRecord user, string challenge, string code, CancellationToken cancellationToken)
    {
        if (!WellFormed(code))
        {
            return null;
        }

        var hash = Tokens.Sha256Hex(challenge);
        var consumed = await this.ConsumeAsync(user.InstanceId, "enrollment-inbox:" + hash, code.Trim(), PasscodePurpose.EnrollmentInbox,
            value => value.UserId == user.Id && value.Email == user.Email && value.AuthEpoch == user.AuthEpoch
                && value.AuthStamp == user.AuthStamp && value.PendingTokenHash == hash, cancellationToken).ConfigureAwait(false);
        return consumed is { Binding: { } binding }
            ? user with { InboxProven = true, AuthenticationBindings = [.. user.AuthenticationBindings, .. consumed.IssuanceBindings, binding] } : null;
    }

    public async Task<IssuedPasscode?> RequestAsync(string instanceId, string email, PasscodePurpose purpose, CancellationToken cancellationToken)
    {
        if (purpose is PasscodePurpose.SignUp)
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), "Sign-up codes are requested with RequestSignUpAsync.");
        }

        return await users.FindByEmailAsync(instanceId, email, cancellationToken).ConfigureAwait(false) is { } user
            ? await this.IssueAsync(instanceId, user.Id, user.Id, user.Email, user.AuthEpoch, user.AuthStamp, purpose, null, null, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<IssuedPasscode?> RequestSignUpAsync(string instanceId, string email, string passwordHash, string pendingToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        ArgumentException.ThrowIfNullOrEmpty(pendingToken);
        if (Users.NormalizeEmail(email) is not { } normalized)
        {
            return null;
        }

        if (await restrictions.AdmissionAsync(instanceId, normalized, false, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        return await users.FindByEmailAsync(instanceId, normalized, cancellationToken).ConfigureAwait(false) is { } user
            ? await this.IssueAsync(instanceId, user.Id, user.Id, user.Email, user.AuthEpoch, user.AuthStamp, PasscodePurpose.SignIn, null, null, cancellationToken).ConfigureAwait(false)
            : await this.IssueAsync(instanceId, SignUpKey + normalized, AuthIds.New(), normalized, 0, null, PasscodePurpose.SignUp, passwordHash,
                Tokens.Sha256Hex(pendingToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IssuedPasscode?> ResendSignUpAsync(string instanceId, string email, CancellationToken cancellationToken = default)
    {
        if (Users.NormalizeEmail(email) is not { } normalized)
        {
            return null;
        }

        if (await restrictions.AdmissionAsync(instanceId, normalized, false, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        if (await users.FindByEmailAsync(instanceId, normalized, cancellationToken).ConfigureAwait(false) is { } user)
        {
            return await this.IssueAsync(instanceId, user.Id, user.Id, user.Email, user.AuthEpoch, user.AuthStamp, PasscodePurpose.SignIn, null, null, cancellationToken).ConfigureAwait(false);
        }

        var previous = (await this.ReadAsync(instanceId, SignUpKey + normalized, cancellationToken).ConfigureAwait(false))?.Value;
        return previous is { Consumed: false, Pruned: false, PasswordHash: { } hash, PendingTokenHash: { } tokenHash }
            && previous.Purpose == StoredPurpose(PasscodePurpose.SignUp)
            ? await this.IssueAsync(instanceId, SignUpKey + normalized, previous.UserId, normalized, 0, null, PasscodePurpose.SignUp, hash, tokenHash, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<IssuedSession?> VerifyAsync(string instanceId, string email, string code, CancellationToken cancellationToken = default)
    {
        if (await this.RedeemAsync(instanceId, email, code, PasscodePurpose.SignIn, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            return null;
        }

        try
        {
            return await sessions.CreateAsync(user, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException error) when (error.Status == 401 && error.Code == "invalid_credentials")
        {
            return null;
        }
    }

    public async Task<UserRecord?> RedeemAsync(string instanceId, string email, string code, PasscodePurpose purpose, CancellationToken cancellationToken = default)
    {
        if (purpose is not PasscodePurpose.SignIn || !WellFormed(code)
            || await users.FindByEmailAsync(instanceId, email, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            return null;
        }

        var consumed = await this.ConsumeAsync(instanceId, user.Id, code.Trim(), purpose,
            passcode => passcode.UserId == user.Id && passcode.Email == user.Email && passcode.AuthEpoch == user.AuthEpoch && passcode.AuthStamp == user.AuthStamp,
            cancellationToken).ConfigureAwait(false);
        return consumed?.AuthenticatedUser ?? (consumed is { Binding: { } binding }
            ? user with { AuthenticationBindings = [.. user.AuthenticationBindings, .. consumed.IssuanceBindings, binding] } : null);
    }

    public async Task<UserRecord?> ResetPasswordAsync(string instanceId, string email, string code, string passwordHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        if (!WellFormed(code) || await users.FindByEmailAsync(instanceId, email, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            return null;
        }

        return (await this.ConsumeAsync(instanceId, user.Id, code.Trim(), PasscodePurpose.PasswordReset,
            passcode => passcode.UserId == user.Id && passcode.Email == user.Email && passcode.AuthEpoch == user.AuthEpoch && passcode.AuthStamp == user.AuthStamp,
            cancellationToken, passwordHash).ConfigureAwait(false))?.AuthenticatedUser;
    }

    public async Task<UserRecord?> RedeemSignUpAsync(string instanceId, string email, string code, string pendingToken, CancellationToken cancellationToken = default)
    {
        var token = pendingToken is { Length: > 0 and <= 256 } ? Tokens.Sha256Hex(pendingToken) : null;
        if (token is null || !WellFormed(code) || Users.NormalizeEmail(email) is not { } normalized
            || await this.ConsumeAsync(instanceId, SignUpKey + normalized, code.Trim(), PasscodePurpose.SignUp,
                passcode => passcode.Email == normalized && passcode.AuthEpoch == 0 && passcode.PasswordHash is not null
                    && passcode.PendingTokenHash is { } stored && Tokens.FixedTimeEquals(stored, token), cancellationToken,
                // The issuance fixes the account id: replaying one sign-up in two regions cannot create two different owners.
                // The account is created in the redemption's own transaction (one remote-durable write, not two).
                createAccount: async passcode => users.NewAccount(instanceId, normalized, passcode.PasswordHash, passcode.UserId,
                    await outbox.CreateAsync(instanceId, "user.signed_up", passcode.UserId, normalized, null, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false) is not { } consumed)
        {
            return null;
        }

        return consumed.AuthenticatedUser;
    }

    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetUtcNow() - IssueWindow - Lifetime;
        return await data.InBatchesAsync(batchSize, (db, token) =>
        {
            var version = AuthIds.New();
            var batch = db.Passcodes.Where(passcode => passcode.WindowStartedAt <= cutoff && !passcode.Pruned)
                .OrderBy(passcode => passcode.WindowStartedAt).Select(passcode => EF.Property<string>(passcode, AuthDbContext.VersionProperty)).Take(batchSize);
            return db.Passcodes.Where(passcode => passcode.WindowStartedAt <= cutoff && !passcode.Pruned && batch.Contains(EF.Property<string>(passcode, AuthDbContext.VersionProperty)))
                .ExecuteUpdateAsync(passcode => passcode
                    .SetProperty(row => row.CodeHash, string.Empty)
                    .SetProperty(row => row.PasswordHash, (string?)null)
                    .SetProperty(row => row.PendingTokenHash, (string?)null)
                    .SetProperty(row => row.Consumed, true)
                    .SetProperty(row => row.Pruned, true)
                    .SetProperty(row => EF.Property<string>(row, AuthDbContext.VersionProperty), version), token);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> IsCurrentAsync(string instanceId, PasscodePurpose purpose, string key, string issueId, CancellationToken cancellationToken)
    {
        return (await this.ReadAsync(instanceId, key, cancellationToken).ConfigureAwait(false))?.Value is { } passcode
            && (passcode.Purpose == StoredPurpose(PasscodePurpose.StepUp)) == (purpose == PasscodePurpose.StepUp)
            && (purpose != PasscodePurpose.EnrollmentInbox || passcode.Purpose == StoredPurpose(purpose))
            && passcode.IssueId == issueId && !passcode.Consumed && passcode.FailedAttempts < MaxAttempts && passcode.ExpiresAt > clock.GetUtcNow();
    }

    public static bool WellFormed(string? code) => code?.Trim() is { Length: 6 } trimmed && trimmed.All(char.IsAsciiDigit);

    private static string? StoredPurpose(PasscodePurpose purpose) => purpose switch
    {
        PasscodePurpose.PasswordReset => "password-reset",
        PasscodePurpose.SignUp => "sign-up",
        PasscodePurpose.StepUp => "step-up",
        PasscodePurpose.EnrollmentInbox => "enrollment-inbox",
        _ => null,
    };

    // Every timestamp, including existing non-rounded records, identifies its containing UTC clock hour.
    private static DateTimeOffset IssueWindowStart(DateTimeOffset timestamp) =>
        new(timestamp.UtcTicks / IssueWindow.Ticks * IssueWindow.Ticks, TimeSpan.Zero);

    private async Task<IssuedPasscode?> IssueAsync(string instanceId, string key, string userId, string email, long authEpoch, string? authStamp, PasscodePurpose purpose,
        string? passwordHash, string? pendingTokenHash, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var rowData = await this.ReadAsync(instanceId, key, cancellationToken).ConfigureAwait(false);
        var previous = rowData?.Value;
        if (purpose is PasscodePurpose.StepUp or PasscodePurpose.EnrollmentInbox && previous is not null)
        {
            return null;
        }

        if (previous is not null && (previous.InstanceId != instanceId || previous.Email != email
            || (purpose is not PasscodePurpose.SignUp && previous.UserId != userId)))
        {
            return null;
        }

        var windowStart = IssueWindowStart(now);
        var previousWindowStart = previous is null ? windowStart : IssueWindowStart(previous.WindowStartedAt);
        var window = previousWindowStart >= windowStart ? previous : null;
        if (window?.IssuedInWindow >= MaxIssuesPerWindow)
        {
            return null;
        }

        var sameState = previous is not null && previous.AuthEpoch == authEpoch && previous.AuthStamp == authStamp;
        var ancestors = sameState ? previous!.IssuanceBindings : [];
        // shortcut: reserve two proofs for state and redemption; a fresh code renews state instead of dropping proofs.
        var recovery = sameState && (previous!.ResetsAuthentication || ancestors.Count >= Credentials.MaxBindings - 2
            || !await credentials.AreCurrentAsync(instanceId, ancestors, cancellationToken).ConfigureAwait(false));
        var issuance = recovery ? null : Credentials.New("passcode-issuance",
            AuthIds.Derived(instanceId, key, AuthIds.Derived(instanceId, authStamp ?? "initial",
                sameState ? previous!.IssueId.Length > 0 ? previous.IssueId : previous.CodeHash : "initial")));
        // The record holds one code: a resend replaces it, so a superseded code never redeems (QA round 4, auth:5).
        var code = Tokens.RandomDigits(6)!;
        var passcode = new PasscodeRecord(instanceId, userId, email, this.Digest(instanceId, userId, code), authEpoch, now + Lifetime,
            FailedAttempts: 0, Consumed: false, window is null ? windowStart : previousWindowStart, (window?.IssuedInWindow ?? 0) + 1, StoredPurpose(purpose), passwordHash, pendingTokenHash)
        {
            IssueId = issuance?.OwnerId ?? AuthIds.New(),
            Key = key,
            AuthStamp = authStamp,
            IssuanceBindings = issuance is null ? [] : [.. ancestors, issuance],
            ResetsAuthentication = recovery,
            StepUpRequestId = purpose == PasscodePurpose.StepUp ? key : null,
        };
        List<Change> changes = [rowData is null ? Change.Insert(passcode) : Change.Update(rowData, passcode)];
        if (issuance is not null)
        {
            changes.Add(Credentials.Claim(instanceId, issuance));
        }

        return await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false)
            ? new IssuedPasscode(email, code, passcode.ExpiresAt, purpose) { Tenant = instanceId, Key = key, IssueId = passcode.IssueId } : null;
    }

    private async Task<PasscodeRecord?> ConsumeAsync(string instanceId, string key, string code, PasscodePurpose purpose, Func<PasscodeRecord, bool> belongs,
        CancellationToken cancellationToken, string? passwordHash = null, Func<PasscodeRecord, Task<(UserRecord User, Change[] Changes)>>? createAccount = null)
    {
        // A round loses its write only to another charged guess, a redemption or a reissue, each bounded, so these
        // rounds reach a verdict under any realistic contention. Running out reveals nothing and charges nothing.
        for (var round = 0; round < MaxAttempts + 2; round++)
        {
            var rowData = await this.ReadAsync(instanceId, key, cancellationToken).ConfigureAwait(false);
            var passcode = rowData?.Value;
            var now = clock.GetUtcNow();
            if (passcode is null || passcode.Consumed || passcode.FailedAttempts >= MaxAttempts || passcode.ExpiresAt <= now
                || passcode.InstanceId != instanceId || passcode.Purpose != StoredPurpose(purpose) || !belongs(passcode)
                || (!passcode.ResetsAuthentication && purpose is not PasscodePurpose.PasswordReset
                    && !await credentials.AreCurrentAsync(instanceId, passcode.IssuanceBindings, cancellationToken).ConfigureAwait(false)))
            {
                return null;
            }

            var digest = this.Digest(instanceId, passcode.UserId, code);
            var matches = Tokens.FixedTimeEquals(passcode.CodeHash, digest);
            var charged = matches ? passcode with { Consumed = true, PasswordHash = null } : passcode with { FailedAttempts = passcode.FailedAttempts + 1 };
            var binding = matches ? Credentials.New("passcode", AuthIds.Derived(instanceId, key, passcode.IssueId.Length > 0 ? passcode.IssueId : passcode.CodeHash)) : null;
            List<Change> changes = [Change.Update(rowData!, charged)];
            if (binding is not null)
            {
                changes.Add(Credentials.Claim(instanceId, binding));
            }

            UserRecord? authenticated = null;
            if (matches && (purpose is PasscodePurpose.PasswordReset || (passcode.ResetsAuthentication && purpose is not (PasscodePurpose.SignUp or PasscodePurpose.StepUp or PasscodePurpose.EnrollmentInbox))))
            {
                if (await users.GetAsync(instanceId, passcode.UserId, cancellationToken).ConfigureAwait(false) is not { } current
                    || current.AuthEpoch != passcode.AuthEpoch || current.AuthStamp != passcode.AuthStamp)
                {
                    return null;
                }

                authenticated = Users.BindAuthenticationState(current with
                {
                    PasswordHash = passwordHash ?? current.PasswordHash,
                    AuthEpoch = checked(current.AuthEpoch + 1),
                    AuthStamp = AuthIds.New(),
                });
                changes.Add(Change.Update(authenticated));
            }

            UserRecord? created = null;
            if (matches && createAccount is not null)
            {
                if (await restrictions.AdmissionAsync(instanceId, passcode.Email, true, cancellationToken).ConfigureAwait(false) is not { } admission)
                {
                    return null;
                }

                changes.AddRange(admission);
                (created, var creates) = await createAccount(passcode).ConfigureAwait(false);
                changes.AddRange(creates);
            }

            if (!await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false))
            {
                if (created is not null)
                {
                    return null;
                }

                continue;
            }

            if (created is not null)
            {
                authenticated = Users.BindAuthenticationState(created);
            }

            // A write that completes after expiry grants nothing.
            return matches && passcode.ExpiresAt > clock.GetUtcNow() ? passcode with { Binding = binding, AuthenticatedUser = authenticated } : null;
        }

        return null;
    }

    private Task<Versioned<PasscodeRecord>?> ReadAsync(string instanceId, string key, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.Passcodes.Where(passcode => passcode.InstanceId == instanceId && passcode.Key == key).WithVersion().SingleOrDefaultAsync(token), cancellationToken);

    private string Digest(string instanceId, string userId, string code) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(this.digestKey,
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{instanceId.Length}:{instanceId}{userId.Length}:{userId}{code}"))));
}
