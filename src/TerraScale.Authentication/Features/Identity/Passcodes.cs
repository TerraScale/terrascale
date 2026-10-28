using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Features.Identity;

/// <summary>What an emailed code does once redeemed; it also selects the email's wording.</summary>
public enum PasscodePurpose
{
    /// <summary>Signs an existing account in.</summary>
    SignIn,

    /// <summary>Lets an existing account set a new password.</summary>
    PasswordReset,

    /// <summary>Creates the account a sign-up asked for.</summary>
    SignUp,

    /// <summary>Reauthenticates one purpose-bound step-up request.</summary>
    StepUp,
}

/// <summary>A freshly issued passcode for the caller to email. It is not stored and cannot be recovered.</summary>
/// <param name="Email">The account's address.</param>
/// <param name="Code">The six-digit code.</param>
/// <param name="ExpiresAt">When it stops working.</param>
/// <param name="Purpose">What the code does.</param>
public sealed record IssuedPasscode(string Email, string Code, DateTimeOffset ExpiresAt, PasscodePurpose Purpose = PasscodePurpose.SignIn)
{
    /// <summary>Server-resolved instance identity for mail accounting.</summary>
    public string Tenant { get; init; } = "platform";

    /// <summary>The store key the code lives under (user id, pending sign-up or step-up request).</summary>
    public string Key { get; init; } = "";

    /// <summary>This issuance; a later code under <see cref="Key"/> replaces it.</summary>
    public string IssueId { get; init; } = "";

    /// <summary>Omits the code and address, so formatting or logging the record never discloses them.</summary>
    public override string ToString() => nameof(IssuedPasscode);
}

/// <summary>
/// Six-digit email passcodes: sign-in codes, password-reset codes and sign-up verification codes. Each user has one
/// current code, whatever its purpose, and each pending sign-up (an email without an account) has its own: it expires
/// after <see cref="Lifetime"/>, works once, for its purpose only, and dies after <see cref="MaxAttempts"/> wrong
/// guesses. A new code replaces the previous one, which never redeems again; at most <see cref="MaxIssuesPerWindow"/> codes are issued per user (or pending sign-up) per
/// fixed UTC clock hour. Only an HMAC of the code, keyed from the master key and bound to its instance and user, is
/// stored, so a database copy alone cannot be brute-forced offline. A guess at a live code is answered only after the
/// conditional write that charges it (or consumes the code) wins, so concurrent guesses cannot exceed the attempt limit
/// and concurrent redemptions have one winner. Callers authorize the instance, deliver the code, and answer every
/// request and failure identically whether or not the email has an account.
/// </summary>
public sealed class Passcodes(AuthStore store, Users users, Sessions sessions, TimeProvider clock, AuthOptions options)
{
    /// <summary>Wrong guesses a code survives.</summary>
    public const int MaxAttempts = 5;

    /// <summary>Codes issued per user per <see cref="IssueWindow"/>.</summary>
    public const int MaxIssuesPerWindow = 5;

    /// <summary>How long a code works.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>The issue-limit window, aligned to UTC clock hours.</summary>
    public static readonly TimeSpan IssueWindow = TimeSpan.FromHours(1);

    internal const string SignUpKey = "sign-up:";

    private readonly byte[] digestKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth passcode digests"u8.ToArray());

    /// <summary>Issues a sign-in code; see <see cref="RequestAsync(string, string, PasscodePurpose, CancellationToken)"/>.</summary>
    public Task<IssuedPasscode?> RequestAsync(string instanceId, string email, CancellationToken cancellationToken = default) =>
        this.RequestAsync(instanceId, email, PasscodePurpose.SignIn, cancellationToken);

    /// <summary>Issues a dedicated code bound to a hosted step-up request, sharing the user's mail budget.</summary>
    internal Task<IssuedPasscode?> RequestStepUpAsync(UserRecord user, string requestId, CancellationToken cancellationToken) =>
        this.IssueAsync(user.InstanceId, requestId, user.Id, user.Email, user.AuthEpoch, user.AuthStamp, PasscodePurpose.StepUp,
            null, Tokens.Sha256Hex(requestId), cancellationToken);

    /// <summary>Consumes only this request's step-up code and retains its winning credential lineage.</summary>
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

    /// <summary>
    /// Issues a sign-in or password-reset code that replaces the user's previous code. Returns <see langword="null"/>
    /// when no account owns the email, the user's issue limit is reached, or a concurrent request issued the code instead.
    /// </summary>
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

    /// <summary>
    /// Starts a sign-up for <paramref name="email"/> with an Argon2id <paramref name="passwordHash"/>: issues a code that
    /// creates the account once redeemed together with <paramref name="pendingToken"/> (<see cref="RedeemSignUpAsync"/>),
    /// replacing any earlier pending sign-up of the address. When the address already has an account, issues that account
    /// a sign-in code instead and discards the hash and token, so the caller answers the same either way. Returns
    /// <see langword="null"/> for an invalid address, a reached issue limit or a lost race.
    /// </summary>
    public async Task<IssuedPasscode?> RequestSignUpAsync(string instanceId, string email, string passwordHash, string pendingToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        ArgumentException.ThrowIfNullOrEmpty(pendingToken);
        if (Users.NormalizeEmail(email) is not { } normalized)
        {
            return null;
        }

        return await users.FindByEmailAsync(instanceId, normalized, cancellationToken).ConfigureAwait(false) is { } user
            ? await this.IssueAsync(instanceId, user.Id, user.Id, user.Email, user.AuthEpoch, user.AuthStamp, PasscodePurpose.SignIn, null, null, cancellationToken).ConfigureAwait(false)
            : await this.IssueAsync(instanceId, SignUpKey + normalized, AuthStore.NewId(), normalized, 0, null, PasscodePurpose.SignUp, passwordHash,
                Tokens.Sha256Hex(pendingToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Redeems a sign-in code and opens a session. Returns <see langword="null"/> for an unknown email and for a wrong,
    /// expired, used, exhausted or superseded code alike. The code is consumed before the session is written, so a
    /// failed session write costs the user a new code. Concurrent regions can issue provisional sessions; only the winning
    /// redemption's sessions remain usable after replication.
    /// </summary>
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

    /// <summary>
    /// Redeems a sign-in code and returns the authenticated account, including its winner bindings.
    /// Returns <see langword="null"/> for an unknown email and for a wrong, expired,
    /// used, exhausted, superseded or other-purpose code alike.
    /// </summary>
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

    /// <summary>Atomically consumes a reset code and replaces the matching source password and authentication state.</summary>
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

    /// <summary>
    /// Redeems a sign-up code presented with the pending token of the request that started that sign-up, and creates
    /// the account with the password chosen there. Returns <see langword="null"/> for another requester's token (without
    /// charging a guess), a wrong, expired, used, exhausted or superseded code, or when the address got an account in the
    /// meantime. The code is consumed before the account is written, so a failed write costs a new sign-up.
    /// </summary>
    public async Task<UserRecord?> RedeemSignUpAsync(string instanceId, string email, string code, string pendingToken, CancellationToken cancellationToken = default)
    {
        var token = pendingToken is { Length: > 0 and <= 256 } ? Tokens.Sha256Hex(pendingToken) : null;
        if (token is null || !WellFormed(code) || Users.NormalizeEmail(email) is not { } normalized
            || await this.ConsumeAsync(instanceId, SignUpKey + normalized, code.Trim(), PasscodePurpose.SignUp,
                passcode => passcode.Email == normalized && passcode.AuthEpoch == 0 && passcode.PasswordHash is not null
                    && passcode.PendingTokenHash is { } stored && Tokens.FixedTimeEquals(stored, token), cancellationToken).ConfigureAwait(false) is not { } consumed)
        {
            return null;
        }

        // The issuance fixes the account id: replaying one sign-up in two regions cannot create two different owners.
        var user = await users.CreateAccountAsync(instanceId, normalized, consumed.PasswordHash, consumed.UserId, cancellationToken).ConfigureAwait(false);
        // Account creation selects its scoped email owner and user state; separate claim clocks cannot choose its state.
        return user;
    }

    /// <summary>
    /// Clears expired code material in batches of <paramref name="batchSize"/>, preserving the issuance anchor so
    /// a later request cannot reuse an earlier immutable claim. Returns how many records this call cleared.
    /// </summary>
    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        // Windows start at UTC instants, so their ISO 8601 text orders chronologically. Every code issued in a window
        // expires by its start + IssueWindow + Lifetime.
        var cutoff = JsonSerializer.Serialize(clock.GetUtcNow() - IssueWindow - Lifetime, IdentityJson.Default.DateTimeOffset);
        using var filter = JsonDocument.Parse($$$"""{"and":[{"lte":["/windowStartedAt",{{{cutoff}}}]},{"not":{"eq":["/pruned",true]}}]}""");
        var total = 0;
        while (true)
        {
            var batch = await store.QueryAsync(AuthStore.PasscodeCollection, filter.RootElement, batchSize, cancellationToken).ConfigureAwait(false);
            var deleted = 0;
            foreach (var document in batch)
            {
                if (document.Document.Deserialize(IdentityJson.Default.PasscodeRecord) is { } passcode
                    && await store.CompareAndPutAsync(AuthStore.PasscodeCollection, passcode.InstanceId,
                        passcode.Purpose == StoredPurpose(PasscodePurpose.SignUp) ? SignUpKey + passcode.Email : passcode.UserId,
                        JsonSerializer.SerializeToElement(passcode with { CodeHash = string.Empty, PasswordHash = null, PendingTokenHash = null, Consumed = true, Pruned = true }, IdentityJson.Default.PasscodeRecord),
                        document.Version, cancellationToken).ConfigureAwait(false))
                {
                    deleted++;
                }
            }

            total += deleted;
            // As for sessions: a full batch may hide more, and a batch that deleted nothing ends the pass.
            if (batch.Count < batchSize || deleted == 0)
            {
                return total;
            }
        }
    }

    /// <summary>
    /// Whether the code issued as <paramref name="issueId"/> under <paramref name="key"/> still redeems: not replaced,
    /// consumed, exhausted or expired. Mail delivery checks it right before sending, so a stale code is never delivered.
    /// </summary>
    internal async Task<bool> IsCurrentAsync(string instanceId, PasscodePurpose purpose, string key, string issueId, CancellationToken cancellationToken)
    {
        var collection = purpose == PasscodePurpose.StepUp ? AuthStore.StepUpPasscodeCollection : AuthStore.PasscodeCollection;
        return (await store.GetAsync(collection, instanceId, key, cancellationToken).ConfigureAwait(false))?.Document.Deserialize(IdentityJson.Default.PasscodeRecord) is { } passcode
            && passcode.IssueId == issueId && !passcode.Consumed && passcode.FailedAttempts < MaxAttempts && passcode.ExpiresAt > clock.GetUtcNow();
    }

    /// <summary>Whether <paramref name="code"/> has the shape of an issued code (six digits, surrounding spaces allowed).</summary>
    public static bool WellFormed(string? code) => code?.Trim() is { Length: 6 } trimmed && trimmed.All(char.IsAsciiDigit);

    private static string? StoredPurpose(PasscodePurpose purpose) => purpose switch
    {
        PasscodePurpose.PasswordReset => "password-reset",
        PasscodePurpose.SignUp => "sign-up",
        PasscodePurpose.StepUp => "step-up",
        _ => null,
    };

    // Every timestamp, including existing non-rounded records, identifies its containing UTC clock hour.
    private static DateTimeOffset IssueWindowStart(DateTimeOffset timestamp) =>
        new(timestamp.UtcTicks / IssueWindow.Ticks * IssueWindow.Ticks, TimeSpan.Zero);

    private async Task<IssuedPasscode?> IssueAsync(string instanceId, string key, string userId, string email, long authEpoch, string? authStamp, PasscodePurpose purpose,
        string? passwordHash, string? pendingTokenHash, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var collection = purpose == PasscodePurpose.StepUp ? AuthStore.StepUpPasscodeCollection : AuthStore.PasscodeCollection;
        var document = await store.GetAsync(collection, instanceId, key, cancellationToken).ConfigureAwait(false);
        var previous = document?.Document.Deserialize(IdentityJson.Default.PasscodeRecord);
        // One isolated issuance per step-up attempt. Sign-in ancestry and its recovery rollover never enter this row.
        if (purpose == PasscodePurpose.StepUp && previous is not null)
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
        var recovery = sameState && (previous!.ResetsAuthentication || ancestors.Count >= AuthStore.MaxCredentialBindings - 2
            || !await store.AreCredentialsCurrentAsync(instanceId, ancestors, cancellationToken).ConfigureAwait(false));
        var issuance = recovery ? null : AuthStore.NewCredentialBinding("passcode-issuance",
            AuthStore.NaturalKeyId(instanceId, key, AuthStore.NaturalKeyId(instanceId, authStamp ?? "initial",
                sameState ? previous!.IssueId.Length > 0 ? previous.IssueId : previous.CodeHash : "initial")));
        // The record holds one code: a resend replaces it, so a superseded code never redeems (QA round 4, auth:5).
        var code = Tokens.RandomDigits(6)!;
        var passcode = new PasscodeRecord(instanceId, userId, email, this.Digest(instanceId, userId, code), authEpoch, now + Lifetime,
            FailedAttempts: 0, Consumed: false, window is null ? windowStart : previousWindowStart, (window?.IssuedInWindow ?? 0) + 1, StoredPurpose(purpose), passwordHash, pendingTokenHash)
        {
            IssueId = issuance?.OwnerId ?? AuthStore.NewId(),
            AuthStamp = authStamp,
            IssuanceBindings = issuance is null ? [] : [.. ancestors, issuance],
            ResetsAuthentication = recovery,
            StepUpRequestId = purpose == PasscodePurpose.StepUp ? key : null,
        };
        var record = JsonSerializer.SerializeToElement(passcode, IdentityJson.Default.PasscodeRecord);
        List<StoreOperation> operations = [document is null
            ? AuthStore.Create(collection, instanceId, key, record)
            : AuthStore.Replace(collection, instanceId, key, record, document.Version)];
        if (issuance is not null)
        {
            operations.Add(AuthStore.CredentialClaim(instanceId, issuance));
        }

        return await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is null
            ? new IssuedPasscode(email, code, passcode.ExpiresAt, purpose) { Tenant = instanceId, Key = key, IssueId = passcode.IssueId } : null;
    }

    /// <summary>
    /// Charges one guess at the live code stored under <paramref name="key"/>, or consumes it when the guess matches.
    /// Returns the code as it was before consumption (a pending sign-up's password hash included; the stored copy drops
    /// it), or <see langword="null"/>.
    /// </summary>
    private async Task<PasscodeRecord?> ConsumeAsync(string instanceId, string key, string code, PasscodePurpose purpose, Func<PasscodeRecord, bool> belongs,
        CancellationToken cancellationToken, string? passwordHash = null)
    {
        var collection = purpose == PasscodePurpose.StepUp ? AuthStore.StepUpPasscodeCollection : AuthStore.PasscodeCollection;
        // A round loses its write only to another charged guess, a redemption or a reissue, each bounded, so these
        // rounds reach a verdict under any realistic contention. Running out reveals nothing and charges nothing.
        for (var round = 0; round < MaxAttempts + 2; round++)
        {
            var document = await store.GetAsync(collection, instanceId, key, cancellationToken).ConfigureAwait(false);
            var passcode = document?.Document.Deserialize(IdentityJson.Default.PasscodeRecord);
            var now = clock.GetUtcNow();
            if (passcode is null || passcode.Consumed || passcode.FailedAttempts >= MaxAttempts || passcode.ExpiresAt <= now
                || passcode.InstanceId != instanceId || passcode.Purpose != StoredPurpose(purpose) || !belongs(passcode)
                || (!passcode.ResetsAuthentication && purpose is not PasscodePurpose.PasswordReset
                    && !await store.AreCredentialsCurrentAsync(instanceId, passcode.IssuanceBindings, cancellationToken).ConfigureAwait(false)))
            {
                return null;
            }

            var digest = this.Digest(instanceId, passcode.UserId, code);
            var matches = Tokens.FixedTimeEquals(passcode.CodeHash, digest);
            var charged = matches ? passcode with { Consumed = true, PasswordHash = null } : passcode with { FailedAttempts = passcode.FailedAttempts + 1 };
            var binding = matches ? AuthStore.NewCredentialBinding("passcode", AuthStore.NaturalKeyId(instanceId, key, passcode.IssueId.Length > 0 ? passcode.IssueId : passcode.CodeHash)) : null;
            List<StoreOperation> operations = [AuthStore.Replace(collection, instanceId, key,
                JsonSerializer.SerializeToElement(charged, IdentityJson.Default.PasscodeRecord), document!.Version)];
            if (binding is not null)
            {
                operations.Add(AuthStore.CredentialClaim(instanceId, binding));
            }

            UserRecord? authenticated = null;
            if (matches && (purpose is PasscodePurpose.PasswordReset || (passcode.ResetsAuthentication && purpose is not (PasscodePurpose.SignUp or PasscodePurpose.StepUp))))
            {
                if (await users.GetVersionedAsync(instanceId, passcode.UserId, cancellationToken).ConfigureAwait(false) is not { } current
                    || current.User.AuthEpoch != passcode.AuthEpoch || current.User.AuthStamp != passcode.AuthStamp)
                {
                    return null;
                }

                // User state is the reset winner; separate document claim clocks do not select the password.
                authenticated = Users.BindAuthenticationState(current.User with
                {
                    PasswordHash = passwordHash ?? current.User.PasswordHash,
                    AuthEpoch = checked(current.User.AuthEpoch + 1),
                    AuthStamp = AuthStore.NewId(),
                });
                operations.Add(AuthStore.Replace(AuthStore.UserCollection, instanceId, current.User.Id,
                    JsonSerializer.SerializeToElement(authenticated, IdentityJson.Default.UserRecord), current.Version));
            }

            if (await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }

            // A write that completes after expiry grants nothing.
            return matches && passcode.ExpiresAt > clock.GetUtcNow() ? passcode with { Binding = binding, AuthenticatedUser = authenticated } : null;
        }

        return null;
    }

    private string Digest(string instanceId, string userId, string code) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(this.digestKey,
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{instanceId.Length}:{instanceId}{userId.Length}:{userId}{code}"))));
}
