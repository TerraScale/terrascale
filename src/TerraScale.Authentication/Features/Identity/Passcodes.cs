using Dapper;
using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Features.SignIn;
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
    Social,
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

    private static readonly string DecoyDigest = new('0', 64);

    private readonly byte[] digestKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth passcode digests"u8.ToArray());

    public Task<IssuedPasscode?> RequestAsync(string instanceId, string email, CancellationToken cancellationToken = default) =>
        this.RequestAsync(instanceId, email, PasscodePurpose.SignIn, cancellationToken);

    internal Task<IssuedPasscode?> RequestStepUpAsync(UserRecord user, string requestId, CancellationToken cancellationToken) =>
        this.IssueAsync(user.InstanceId, requestId, user.Id, user.Email, user.AuthEpoch, user.AuthStamp, PasscodePurpose.StepUp,
            null, Tokens.Sha256Hex(requestId), cancellationToken: cancellationToken);

    internal async Task<UserRecord?> RedeemStepUpAsync(UserRecord user, string requestId, string code, CancellationToken cancellationToken)
    {
        if (!WellFormed(code))
        {
            return null;
        }

        var consumed = await this.ConsumeAsync(user.InstanceId, requestId, code.Trim(), PasscodePurpose.StepUp,
            value => value.UserId == user.Id && value.AuthEpoch == user.AuthEpoch && value.AuthStamp == user.AuthStamp
                && value.PendingTokenHash == Tokens.Sha256Hex(requestId), cancellationToken: cancellationToken);
        return consumed?.AuthenticatedUser ?? (consumed is { Binding: { } binding }
            ? user with { AuthenticationBindings = [.. user.AuthenticationBindings, .. consumed.IssuanceBindings, binding] } : null);
    }

    internal Task<IssuedPasscode?> RequestEnrollmentInboxAsync(UserRecord user, string challenge, CancellationToken cancellationToken) =>
        this.IssueAsync(user.InstanceId, "enrollment-inbox:" + Tokens.Sha256Hex(challenge), user.Id, user.Email, user.AuthEpoch, user.AuthStamp,
            PasscodePurpose.EnrollmentInbox, null, Tokens.Sha256Hex(challenge), cancellationToken: cancellationToken);

    internal async Task<UserRecord?> RedeemEnrollmentInboxAsync(UserRecord user, string challenge, string code, CancellationToken cancellationToken)
    {
        if (!WellFormed(code))
        {
            return null;
        }

        var hash = Tokens.Sha256Hex(challenge);
        var consumed = await this.ConsumeAsync(user.InstanceId, "enrollment-inbox:" + hash, code.Trim(), PasscodePurpose.EnrollmentInbox,
            value => value.UserId == user.Id && value.Email == user.Email && value.AuthEpoch == user.AuthEpoch
                && value.AuthStamp == user.AuthStamp && value.PendingTokenHash == hash, cancellationToken);
        return consumed is { Binding: { } binding }
            ? user with { InboxProven = true, AuthenticationBindings = [.. user.AuthenticationBindings, .. consumed.IssuanceBindings, binding] } : null;
    }

    public async Task<IssuedPasscode?> RequestAsync(string instanceId, string email, PasscodePurpose purpose, CancellationToken cancellationToken)
    {
        if (purpose is PasscodePurpose.SignUp)
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), "Sign-up codes are requested with RequestSignUpAsync.");
        }

        return await users.FindByEmailAsync(instanceId, email, cancellationToken) is { } user
            ? await this.IssueAsync(instanceId, user.Id, user.Id, user.Email, user.AuthEpoch, user.AuthStamp, purpose, null, null, cancellationToken: cancellationToken)
            : null;
    }

    public async Task<IssuedPasscode?> RequestSignUpAsync(string instanceId, string email, string passwordHash, string pendingToken, string? username = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        ArgumentException.ThrowIfNullOrEmpty(pendingToken);
        if (Users.NormalizeEmail(email) is not { } normalized)
        {
            return null;
        }

        if (await restrictions.AdmissionAsync(instanceId, normalized, false, cancellationToken) is null)
        {
            return null;
        }

        return await users.FindByEmailAsync(instanceId, normalized, cancellationToken) is { } user
            ? await this.IssueAsync(instanceId, user.Id, user.Id, user.Email, user.AuthEpoch, user.AuthStamp, PasscodePurpose.SignIn, null, null, cancellationToken: cancellationToken)
            : await this.IssueAsync(instanceId, SignUpKey + normalized, AuthIds.New(), normalized, 0, null, PasscodePurpose.SignUp, passwordHash,
                Tokens.Sha256Hex(pendingToken), cancellationToken: cancellationToken, username: username);
    }

    public async Task<IssuedPasscode?> ResendSignUpAsync(string instanceId, string email, CancellationToken cancellationToken = default)
    {
        if (Users.NormalizeEmail(email) is not { } normalized)
        {
            return null;
        }

        if (await restrictions.AdmissionAsync(instanceId, normalized, false, cancellationToken) is null)
        {
            return null;
        }

        if (await users.FindByEmailAsync(instanceId, normalized, cancellationToken) is { } user)
        {
            return await this.IssueAsync(instanceId, user.Id, user.Id, user.Email, user.AuthEpoch, user.AuthStamp, PasscodePurpose.SignIn, null, null, cancellationToken: cancellationToken);
        }

        var previous = (await this.ReadAsync(instanceId, SignUpKey + normalized, cancellationToken))?.Value;
        return previous is { Consumed: false, Pruned: false, PasswordHash: { } hash, PendingTokenHash: { } tokenHash }
            && previous.Purpose == StoredPurpose(PasscodePurpose.SignUp)
            ? await this.IssueAsync(instanceId, SignUpKey + normalized, previous.UserId, normalized, 0, null, PasscodePurpose.SignUp, hash, tokenHash, cancellationToken: cancellationToken, username: previous.Username)
            : null;
    }

    public async Task<IssuedSession?> VerifyAsync(string instanceId, string email, string code, CancellationToken cancellationToken = default)
    {
        if (await this.RedeemAsync(instanceId, email, code, PasscodePurpose.SignIn, cancellationToken: cancellationToken) is not { } user)
        {
            return null;
        }

        try
        {
            return await sessions.CreateAsync(user, cancellationToken);
        }
        catch (ApiException error) when (error.Status == 401 && error.Code == "invalid_credentials")
        {
            return null;
        }
    }

    public async Task<UserRecord?> RedeemAsync(string instanceId, string email, string code, PasscodePurpose purpose, string? issueId = null, CancellationToken cancellationToken = default)
    {
        if (purpose is not PasscodePurpose.SignIn || !WellFormed(code) || Users.NormalizeEmail(email) is not { } normalized)
        {
            return null;
        }

        if (await users.FindByEmailAsync(instanceId, normalized, cancellationToken) is not { } user)
        {
            return await this.DecoyAsync(instanceId, normalized, code.Trim(), cancellationToken);
        }

        var consumed = await this.ConsumeAsync(instanceId, user.Id, code.Trim(), purpose,
            passcode => passcode.UserId == user.Id && passcode.Email == user.Email && passcode.AuthEpoch == user.AuthEpoch && passcode.AuthStamp == user.AuthStamp
                && (issueId is null || passcode.IssueId == issueId),
            cancellationToken);
        return consumed?.AuthenticatedUser ?? (consumed is { Binding: { } binding }
            ? user with { AuthenticationBindings = [.. user.AuthenticationBindings, .. consumed.IssuanceBindings, binding] } : null);
    }

    public async Task<UserRecord?> ResetPasswordAsync(string instanceId, string email, string code, string passwordHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        if (!WellFormed(code) || Users.NormalizeEmail(email) is not { } normalized)
        {
            return null;
        }

        if (await users.FindByEmailAsync(instanceId, normalized, cancellationToken) is not { } user)
        {
            return await this.DecoyAsync(instanceId, normalized, code.Trim(), cancellationToken);
        }

        return (await this.ConsumeAsync(instanceId, user.Id, code.Trim(), PasscodePurpose.PasswordReset,
            passcode => passcode.UserId == user.Id && passcode.Email == user.Email && passcode.AuthEpoch == user.AuthEpoch && passcode.AuthStamp == user.AuthStamp,
            cancellationToken, passwordHash))?.AuthenticatedUser;
    }

    public async Task<UserRecord?> RedeemSignUpAsync(string instanceId, string email, string code, string pendingToken, CancellationToken cancellationToken = default)
    {
        var token = pendingToken is { Length: > 0 and <= 256 } ? Tokens.Sha256Hex(pendingToken) : null;
        if (token is null || !WellFormed(code) || Users.NormalizeEmail(email) is not { } normalized
            || await this.ConsumeAsync(instanceId, SignUpKey + normalized, code.Trim(), PasscodePurpose.SignUp,
                passcode => passcode.Email == normalized && passcode.AuthEpoch == 0 && passcode.PasswordHash is not null
                    && passcode.PendingTokenHash is { } stored && Tokens.FixedTimeEquals(stored, token), cancellationToken,
                createAccount: async passcode => users.NewAccount(instanceId, normalized, passcode.PasswordHash, passcode.UserId,
                    await outbox.CreateAsync(instanceId, "user.signed_up", passcode.UserId, normalized, null, cancellationToken), passcode.Username)) is not { } consumed)
        {
            return null;
        }

        return consumed.AuthenticatedUser;
    }

    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetUtcNow() - IssueWindow - Lifetime;
        return await data.InBatchesAsync(batchSize, connection =>
        {
            var version = AuthIds.New();
            return connection.ExecuteAsync(
                """
                UPDATE passcodes SET code_hash = '', password_hash = NULL, pending_token_hash = NULL, consumed = 1, pruned = 1, version = @version
                WHERE (instance_id, "key") IN (
                    SELECT instance_id, "key" FROM passcodes WHERE window_started_at <= @cutoff AND pruned = 0 ORDER BY window_started_at LIMIT @batchSize)
                """, new { version, cutoff, batchSize });
        }, cancellationToken);
    }

    internal async Task<bool> IsCurrentAsync(string instanceId, PasscodePurpose purpose, string key, string issueId, CancellationToken cancellationToken)
    {
        return (await this.ReadAsync(instanceId, key, cancellationToken))?.Value is { } passcode
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
        PasscodePurpose.SignIn or PasscodePurpose.Social => null,
        _ => throw new UnreachableException(),
    };

    private static DateTimeOffset IssueWindowStart(DateTimeOffset timestamp) =>
        new(timestamp.UtcTicks / IssueWindow.Ticks * IssueWindow.Ticks, TimeSpan.Zero);

    internal async Task<IssuedPasscode?> RequestSocialAsync(string tenant, string token, CancellationToken cancellationToken)
    {
        if (await this.PendingSocialAsync(tenant, token, cancellationToken) is not { } pending)
        {
            return null;
        }

        var user = await users.FindByEmailAsync(tenant, pending.Email, cancellationToken);
        if (user is { Disabled: true } || user is null && await restrictions.AdmissionAsync(tenant, pending.Email, false, cancellationToken) is null)
        {
            return null;
        }

        var previous = (await this.ReadAsync(tenant, "social:" + pending.Id, cancellationToken))?.Value;
        if (previous?.Consumed is true || user is null && previous?.AuthStamp is not null)
        {
            return null;
        }

        return await this.IssueAsync(tenant, "social:" + pending.Id, user?.Id ?? previous?.UserId ?? AuthIds.New(), pending.Email, user?.AuthEpoch ?? 0, user?.AuthStamp,
            PasscodePurpose.Social, null, Tokens.Sha256Hex(token),
            lifetime: DateTimeOffset.FromUnixTimeSeconds(pending.ExpiresAt) - clock.GetUtcNow(), cancellationToken: cancellationToken);
    }

    internal async Task<UserRecord?> RedeemSocialAsync(string tenant, string token, string code, CancellationToken cancellationToken)
    {
        if (!WellFormed(code) || await this.PendingSocialAsync(tenant, token, cancellationToken) is not { } pending)
        {
            return null;
        }

        var user = await users.FindByEmailAsync(tenant, pending.Email, cancellationToken);
        if (user is { Disabled: true } || user is null && await restrictions.AdmissionAsync(tenant, pending.Email, false, cancellationToken) is null)
        {
            return null;
        }

        List<Change> linkChanges = user is null ? [] : [Change.Assert(user), Change.Insert(Users.Link(user, pending.Identity))];
        if (user is not null && await outbox.CreateAsync(tenant, "user.provider_linked", user.Id, user.Email,
            pending.Identity.Provider, cancellationToken) is { } linked)
        {
            linkChanges.Add(linked);
        }

        if (user is not null && await outbox.CreateAsync(tenant, "user.updated", user.Id, user.Email,
            pending.Identity.Provider, cancellationToken) is { } updatedEvent)
        {
            linkChanges.Add(updatedEvent);
        }

        var consumed = await this.ConsumeAsync(tenant, "social:" + pending.Id, code.Trim(), PasscodePurpose.Social,
            passcode => passcode.Email == pending.Email && passcode.PendingTokenHash == Tokens.Sha256Hex(token)
                && (user is null ? passcode.AuthStamp is null : passcode.UserId == user.Id && passcode.AuthEpoch == user.AuthEpoch && passcode.AuthStamp == user.AuthStamp),
            cancellationToken, createAccount: user is null ? async passcode =>
            {
                var (created, changes) = users.NewAccount(tenant, pending.Email, null, passcode.UserId,
                    await outbox.CreateAsync(tenant, "user.signed_up", passcode.UserId, pending.Email, null, cancellationToken));
                return (created, [.. changes, Change.Insert(Users.Link(created, pending.Identity))]);
            }
        : null,
            extraChanges: linkChanges);
        return consumed is null ? null : await users.FindByProviderAsync(tenant, pending.Identity, cancellationToken);
    }

    private async Task<PendingSocialLink?> PendingSocialAsync(string tenant, string token, CancellationToken cancellationToken)
    {
        var pending = PendingSocialLinks.Open(options, tenant, token, clock);
        return pending is not null
            && (await new ProjectAuthSettings(data).GetAsync(tenant, cancellationToken)).Provider(pending.Identity.Provider) is { } provider
            && SocialSignIn.ConfigurationHash(provider) == pending.ConfigurationHash
            ? pending : null;
    }

    public async Task<bool> PendingUsernameTakenAsync(string tenant, string email, string token, CancellationToken cancellationToken)
    {
        var pending = (await this.ReadAsync(tenant, SignUpKey + Users.NormalizeEmail(email), cancellationToken))?.Value;
        return pending is { Username: { } username, PendingTokenHash: { } hash }
            && Tokens.FixedTimeEquals(hash, Tokens.Sha256Hex(token))
            && await users.FindByUsernameAsync(tenant, username, cancellationToken) is not null;
    }

    public async Task<IssuedPasscode?> RequestMagicLinkAsync(string instanceId, string email, CancellationToken cancellationToken) =>
        await users.FindByEmailAsync(instanceId, email, cancellationToken) is { Disabled: false } user
            ? await this.IssueAsync(instanceId, user.Id, user.Id, user.Email, user.AuthEpoch, user.AuthStamp, PasscodePurpose.SignIn, null, null, lifetime: TimeSpan.FromMinutes(10), cancellationToken: cancellationToken)
            : null;

    private async Task<IssuedPasscode?> IssueAsync(string instanceId, string key, string userId, string email, long authEpoch, string? authStamp, PasscodePurpose purpose,
        string? passwordHash, string? pendingTokenHash, TimeSpan? lifetime = null, string? username = null, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var rowData = await this.ReadAsync(instanceId, key, cancellationToken);
        var previous = rowData?.Value;
        if (purpose == PasscodePurpose.Social && previous is { Consumed: true })
        {
            return null;
        }

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
        var recovery = sameState && (previous!.ResetsAuthentication || ancestors.Count >= Credentials.MaxBindings - 2
            || !await credentials.AreCurrentAsync(instanceId, ancestors, cancellationToken));
        var issuance = recovery ? null : Credentials.New("passcode-issuance",
            AuthIds.Derived(instanceId, key, AuthIds.Derived(instanceId, authStamp ?? "initial",
                sameState ? previous!.IssueId.Length > 0 ? previous.IssueId : previous.CodeHash : "initial")));
        var code = Tokens.RandomDigits(6)!;
        var passcode = new PasscodeRecord(instanceId, userId, email, this.Digest(instanceId, userId, code), authEpoch, now + (lifetime ?? Lifetime),
            FailedAttempts: 0, Consumed: false, window is null ? windowStart : previousWindowStart, (window?.IssuedInWindow ?? 0) + 1, StoredPurpose(purpose), passwordHash, pendingTokenHash)
        {
            IssueId = issuance?.OwnerId ?? AuthIds.New(),
            Key = key,
            AuthStamp = authStamp,
            IssuanceBindings = issuance is null ? [] : [.. ancestors, issuance],
            ResetsAuthentication = recovery,
            StepUpRequestId = purpose == PasscodePurpose.StepUp ? key : null,
            Username = username,
        };
        List<Change> changes = [rowData is null ? Change.Insert(passcode) : Change.Update(rowData, passcode)];
        if (issuance is not null)
        {
            changes.Add(Credentials.Claim(instanceId, issuance));
        }

        return await data.SaveAsync(changes, cancellationToken)
            ? new IssuedPasscode(email, code, passcode.ExpiresAt, purpose) { Tenant = instanceId, Key = key, IssueId = passcode.IssueId } : null;
    }

    private async Task<PasscodeRecord?> ConsumeAsync(string instanceId, string key, string code, PasscodePurpose purpose, Func<PasscodeRecord, bool> belongs,
        CancellationToken cancellationToken, string? passwordHash = null, Func<PasscodeRecord, Task<(UserRecord User, Change[] Changes)>>? createAccount = null, IReadOnlyList<Change>? extraChanges = null)
    {
        for (var round = 0; round < MaxAttempts + 2; round++)
        {
            var rowData = await this.ReadAsync(instanceId, key, cancellationToken);
            var passcode = rowData?.Value;
            var now = clock.GetUtcNow();
            if (passcode is null || passcode.Consumed || passcode.FailedAttempts >= MaxAttempts || passcode.ExpiresAt <= now
                || passcode.InstanceId != instanceId || passcode.Purpose != StoredPurpose(purpose) || !belongs(passcode)
                || (!passcode.ResetsAuthentication && purpose is not PasscodePurpose.PasswordReset
                    && !await credentials.AreCurrentAsync(instanceId, passcode.IssuanceBindings, cancellationToken)))
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
            if (matches && (purpose is PasscodePurpose.PasswordReset || (passcode.ResetsAuthentication && purpose is not (PasscodePurpose.SignUp or PasscodePurpose.StepUp or PasscodePurpose.EnrollmentInbox or PasscodePurpose.Social))))
            {
                if (await users.GetAsync(instanceId, passcode.UserId, cancellationToken) is not { } current
                    || current.AuthEpoch != passcode.AuthEpoch || current.AuthStamp != passcode.AuthStamp)
                {
                    return null;
                }

                authenticated = Users.BindAuthenticationState(current with
                {
                    PasswordHash = passwordHash ?? current.PasswordHash,
                    EmailVerified = true,
                    AuthEpoch = checked(current.AuthEpoch + 1),
                    AuthStamp = AuthIds.New(),
                });
                var updateUser = Change.Update(authenticated);
                changes.Add(updateUser);
                authenticated = authenticated with { Version = updateUser.Version };
            }

            UserRecord? created = null;
            if (matches && createAccount is not null)
            {
                if (await restrictions.AdmissionAsync(instanceId, passcode.Email, true, cancellationToken) is not { } admission)
                {
                    return null;
                }

                changes.AddRange(admission);
                (created, var creates) = await createAccount(passcode);
                changes.AddRange(creates);
            }

            if (matches && extraChanges is not null)
            {
                changes.AddRange(extraChanges);
            }

            if (!await data.SaveAsync(changes, cancellationToken))
            {
                if (created is not null || extraChanges is { Count: > 0 })
                {
                    return null;
                }

                continue;
            }

            if (created is not null)
            {
                authenticated = Users.BindAuthenticationState(created);
            }

            return matches && passcode.ExpiresAt > clock.GetUtcNow() ? passcode with { Binding = binding, AuthenticatedUser = authenticated } : null;
        }

        return null;
    }

    private async Task<Versioned<PasscodeRecord>?> ReadAsync(string instanceId, string key, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<PasscodeRecord>(
            "SELECT * FROM passcodes WHERE instance_id = @instanceId AND \"key\" = @key", new { instanceId, key }), cancellationToken));

    private async Task<UserRecord?> DecoyAsync(string instanceId, string normalized, string code, CancellationToken cancellationToken)
    {
        var key = AuthIds.Derived(instanceId, "passcode-decoy", normalized);
        await data.ReadAsync(connection => connection.QueryAsync<string>(
            "SELECT subject FROM provider_links WHERE user_id = @key", new { key }), cancellationToken);
        await this.ReadAsync(instanceId, key, cancellationToken);
        await credentials.AreCurrentAsync(instanceId, [Credentials.UserState(key, key)], cancellationToken);
        _ = Tokens.FixedTimeEquals(this.Digest(instanceId, key, code), DecoyDigest);
        await data.ReadAsync(connection => connection.ExecuteAsync(
            "UPDATE passcodes SET failed_attempts = failed_attempts + 1 WHERE instance_id = @instanceId AND \"key\" = @key", new { instanceId, key }), cancellationToken);
        return null;
    }

    private string Digest(string instanceId, string userId, string code) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(this.digestKey,
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{instanceId.Length}:{instanceId}{userId.Length}:{userId}{code}"))));
}
