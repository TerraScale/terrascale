using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Security;
using TerraScale.Auth.Store;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Auth.Features.Identity;

/// <summary>What an emailed code does once redeemed; it also selects the email's wording.</summary>
public enum PasscodePurpose
{
    /// <summary>Signs an existing account in.</summary>
    SignIn,

    /// <summary>Lets an existing account set a new password.</summary>
    PasswordReset,

    /// <summary>Creates the account a sign-up asked for.</summary>
    SignUp,
}

/// <summary>A freshly issued passcode for the caller to email. It is not stored and cannot be recovered.</summary>
/// <param name="Email">The account's address.</param>
/// <param name="Code">The six-digit code.</param>
/// <param name="ExpiresAt">When it stops working.</param>
/// <param name="Purpose">What the code does.</param>
public sealed record IssuedPasscode(string Email, string Code, DateTimeOffset ExpiresAt, PasscodePurpose Purpose = PasscodePurpose.SignIn)
{
    /// <summary>Omits the code and address, so formatting or logging the record never discloses them.</summary>
    public override string ToString() => nameof(IssuedPasscode);
}

/// <summary>
/// Six-digit email passcodes: sign-in codes, password-reset codes and sign-up verification codes. Each user has one
/// current code, whatever its purpose, and each pending sign-up (an email without an account) has its own: it expires
/// after <see cref="Lifetime"/>, works once, for its purpose only, and dies after <see cref="MaxAttempts"/> wrong
/// guesses; at most <see cref="MaxIssuesPerWindow"/> codes are issued per user (or pending sign-up) per
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
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The issue-limit window, aligned to UTC clock hours.</summary>
    public static readonly TimeSpan IssueWindow = TimeSpan.FromHours(1);

    private const string SignUpKey = "sign-up:";

    private readonly byte[] digestKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth passcode digests"u8.ToArray());

    /// <summary>Issues a sign-in code; see <see cref="RequestAsync(string, string, PasscodePurpose, CancellationToken)"/>.</summary>
    public Task<IssuedPasscode?> RequestAsync(string instanceId, string email, CancellationToken cancellationToken = default) =>
        this.RequestAsync(instanceId, email, PasscodePurpose.SignIn, cancellationToken);

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
            ? await this.IssueAsync(instanceId, user.Id, user.Id, user.Email, user.AuthEpoch, purpose, null, null, cancellationToken).ConfigureAwait(false)
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
            ? await this.IssueAsync(instanceId, user.Id, user.Id, user.Email, user.AuthEpoch, PasscodePurpose.SignIn, null, null, cancellationToken).ConfigureAwait(false)
            : await this.IssueAsync(instanceId, SignUpKey + normalized, Guid.NewGuid().ToString("D"), normalized, 0, PasscodePurpose.SignUp, passwordHash,
                Tokens.Sha256Hex(pendingToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resends a pending sign-up's verification code, preserving its password hash and token binding. A single CAS
    /// against the record read prevents reviving a consumed sign-up or overwriting a replacement. Existing accounts
    /// receive sign-in codes instead; missing records and wrong tokens issue nothing, with the same caller response.
    /// </summary>
    public async Task<IssuedPasscode?> ResendSignUpAsync(string instanceId, string email, string pendingToken, CancellationToken cancellationToken = default)
    {
        if (Users.NormalizeEmail(email) is not { } normalized)
        {
            return null;
        }

        if (await users.FindByEmailAsync(instanceId, normalized, cancellationToken).ConfigureAwait(false) is { } user)
        {
            return await this.IssueAsync(instanceId, user.Id, user.Id, user.Email, user.AuthEpoch, PasscodePurpose.SignIn, null, null, cancellationToken).ConfigureAwait(false);
        }

        var key = SignUpKey + normalized;
        var document = await store.GetAsync(AuthStore.PasscodeCollection, instanceId, key, cancellationToken).ConfigureAwait(false);
        var previous = document?.Document.Deserialize(IdentityJson.Default.PasscodeRecord);
        if (pendingToken is not { Length: > 0 and <= 256 } || previous is null || previous.Consumed || previous.PasswordHash is null
            || previous.InstanceId != instanceId || previous.Email != normalized || previous.AuthEpoch != 0
            || previous.Purpose != StoredPurpose(PasscodePurpose.SignUp) || previous.PendingTokenHash is not { } token
            || !Tokens.FixedTimeEquals(token, Tokens.Sha256Hex(pendingToken)))
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var windowStart = IssueWindowStart(now);
        var previousWindowStart = IssueWindowStart(previous.WindowStartedAt);
        var window = previousWindowStart >= windowStart ? previous : null;
        if (window?.IssuedInWindow >= MaxIssuesPerWindow)
        {
            return null;
        }

        string code;
        string digest;
        do
        {
            code = Tokens.RandomDigits(6)!;
            digest = this.Digest(instanceId, previous.UserId, code);
        }
        while (Tokens.FixedTimeEquals(digest, previous.CodeHash));

        var replacement = previous with
        {
            CodeHash = digest,
            ExpiresAt = now + Lifetime,
            FailedAttempts = 0,
            WindowStartedAt = window is null ? windowStart : previousWindowStart,
            IssuedInWindow = (window?.IssuedInWindow ?? 0) + 1,
        };
        return await store.CompareAndPutAsync(AuthStore.PasscodeCollection, instanceId, key,
            JsonSerializer.SerializeToElement(replacement, IdentityJson.Default.PasscodeRecord), document!.Version, cancellationToken).ConfigureAwait(false)
            ? new IssuedPasscode(normalized, code, replacement.ExpiresAt, PasscodePurpose.SignUp)
            : null;
    }

    /// <summary>
    /// Redeems a sign-in code and opens a session. Returns <see langword="null"/> for an unknown email and for a wrong,
    /// expired, used, exhausted or superseded code alike. The code is consumed before the session is written, so a
    /// failed session write costs the user a new code and never grants a second session.
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
    /// Redeems a sign-in or password-reset code and returns the account as it was when the code was issued (its
    /// authentication epoch included). Returns <see langword="null"/> for an unknown email and for a wrong, expired,
    /// used, exhausted, superseded or other-purpose code alike.
    /// </summary>
    public async Task<UserRecord?> RedeemAsync(string instanceId, string email, string code, PasscodePurpose purpose, CancellationToken cancellationToken = default)
    {
        if (purpose is PasscodePurpose.SignUp || !WellFormed(code)
            || await users.FindByEmailAsync(instanceId, email, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            return null;
        }

        return await this.ConsumeAsync(instanceId, user.Id, code.Trim(), purpose,
            passcode => passcode.UserId == user.Id && passcode.Email == user.Email && passcode.AuthEpoch == user.AuthEpoch, cancellationToken).ConfigureAwait(false) is not null
            ? user
            : null;
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

        return await users.CreateAsync(instanceId, normalized, consumed.PasswordHash, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes code records whose issue window, and so every code issued in it, has ended (pending sign-ups included) in
    /// batches of <paramref name="batchSize"/>, each only at the version read. Such a record can neither redeem nor limit
    /// anything, so deleting it changes no outcome; it keeps storage, and the password hashes of abandoned sign-ups,
    /// proportional to recent requests. Returns how many this call deleted.
    /// </summary>
    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        // Windows start at UTC instants, so their ISO 8601 text orders chronologically. Every code issued in a window
        // expires by its start + IssueWindow + Lifetime.
        var cutoff = JsonSerializer.Serialize(clock.GetUtcNow() - IssueWindow - Lifetime, IdentityJson.Default.DateTimeOffset);
        using var filter = JsonDocument.Parse($$"""{"lte":["/windowStartedAt",{{cutoff}}]}""");
        var total = 0;
        while (true)
        {
            var batch = await store.QueryAsync(AuthStore.PasscodeCollection, filter.RootElement, batchSize, cancellationToken).ConfigureAwait(false);
            var deleted = 0;
            foreach (var document in batch)
            {
                if (document.Document.Deserialize(IdentityJson.Default.PasscodeRecord) is { } passcode
                    && await store.CompareAndDeleteAsync(AuthStore.PasscodeCollection, passcode.InstanceId,
                        passcode.Purpose == StoredPurpose(PasscodePurpose.SignUp) ? SignUpKey + passcode.Email : passcode.UserId, document.Version, cancellationToken).ConfigureAwait(false))
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

    /// <summary>Whether <paramref name="code"/> has the shape of an issued code (six digits, surrounding spaces allowed).</summary>
    public static bool WellFormed(string? code) => code?.Trim() is { Length: 6 } trimmed && trimmed.All(char.IsAsciiDigit);

    private static string? StoredPurpose(PasscodePurpose purpose) => purpose switch
    {
        PasscodePurpose.PasswordReset => "password-reset",
        PasscodePurpose.SignUp => "sign-up",
        _ => null,
    };

    // Every timestamp, including existing non-rounded records, identifies its containing UTC clock hour.
    private static DateTimeOffset IssueWindowStart(DateTimeOffset timestamp) =>
        new(timestamp.UtcTicks / IssueWindow.Ticks * IssueWindow.Ticks, TimeSpan.Zero);

    private async Task<IssuedPasscode?> IssueAsync(string instanceId, string key, string userId, string email, long authEpoch, PasscodePurpose purpose,
        string? passwordHash, string? pendingTokenHash, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var document = await store.GetAsync(AuthStore.PasscodeCollection, instanceId, key, cancellationToken).ConfigureAwait(false);
        var previous = document?.Document.Deserialize(IdentityJson.Default.PasscodeRecord);
        var windowStart = IssueWindowStart(now);
        var previousWindowStart = previous is null ? windowStart : IssueWindowStart(previous.WindowStartedAt);
        var window = previousWindowStart >= windowStart ? previous : null;
        if (window?.IssuedInWindow >= MaxIssuesPerWindow)
        {
            return null;
        }

        var code = Tokens.RandomDigits(6)!;
        var passcode = new PasscodeRecord(instanceId, userId, email, this.Digest(instanceId, userId, code), authEpoch, now + Lifetime,
            FailedAttempts: 0, Consumed: false, window is null ? windowStart : previousWindowStart, (window?.IssuedInWindow ?? 0) + 1, StoredPurpose(purpose), passwordHash, pendingTokenHash);
        var record = JsonSerializer.SerializeToElement(passcode, IdentityJson.Default.PasscodeRecord);
        bool written;
        if (document is null)
        {
            try
            {
                await store.CreateAsync(AuthStore.PasscodeCollection, instanceId, key, record, cancellationToken).ConfigureAwait(false);
                written = true;
            }
            catch (TerraBasePreconditionFailedException error) when (error.ErrorCode == "precondition_failed")
            {
                written = false;
            }
        }
        else
        {
            written = await store.CompareAndPutAsync(AuthStore.PasscodeCollection, instanceId, key, record, document.Version, cancellationToken).ConfigureAwait(false);
        }

        return written ? new IssuedPasscode(email, code, passcode.ExpiresAt, purpose) : null;
    }

    /// <summary>
    /// Charges one guess at the live code stored under <paramref name="key"/>, or consumes it when the guess matches.
    /// Returns the code as it was before consumption (a pending sign-up's password hash included; the stored copy drops
    /// it), or <see langword="null"/>.
    /// </summary>
    private async Task<PasscodeRecord?> ConsumeAsync(string instanceId, string key, string code, PasscodePurpose purpose, Func<PasscodeRecord, bool> belongs,
        CancellationToken cancellationToken)
    {
        // A round loses its write only to another charged guess, a redemption or a reissue, each bounded, so these
        // rounds reach a verdict under any realistic contention. Running out reveals nothing and charges nothing.
        for (var round = 0; round < MaxAttempts + 2; round++)
        {
            var document = await store.GetAsync(AuthStore.PasscodeCollection, instanceId, key, cancellationToken).ConfigureAwait(false);
            var passcode = document?.Document.Deserialize(IdentityJson.Default.PasscodeRecord);
            if (passcode is null || passcode.Consumed || passcode.FailedAttempts >= MaxAttempts || passcode.ExpiresAt <= clock.GetUtcNow()
                || passcode.InstanceId != instanceId || passcode.Purpose != StoredPurpose(purpose) || !belongs(passcode))
            {
                return null;
            }

            var matches = Tokens.FixedTimeEquals(passcode.CodeHash, this.Digest(instanceId, passcode.UserId, code));
            var charged = matches ? passcode with { Consumed = true, PasswordHash = null } : passcode with { FailedAttempts = passcode.FailedAttempts + 1 };
            if (!await store.CompareAndPutAsync(AuthStore.PasscodeCollection, instanceId, key,
                    JsonSerializer.SerializeToElement(charged, IdentityJson.Default.PasscodeRecord), document!.Version, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            // A write that completes after expiry grants nothing.
            return matches && passcode.ExpiresAt > clock.GetUtcNow() ? passcode : null;
        }

        return null;
    }

    private string Digest(string instanceId, string userId, string code) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(this.digestKey,
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{instanceId.Length}:{instanceId}{userId.Length}:{userId}{code}"))));
}
