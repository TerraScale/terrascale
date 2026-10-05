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

/// <summary>A freshly issued passcode for the caller to email. It is not stored and cannot be recovered.</summary>
/// <param name="Email">The account's address.</param>
/// <param name="Code">The six-digit code.</param>
/// <param name="ExpiresAt">When it stops working.</param>
public sealed record IssuedPasscode(string Email, string Code, DateTimeOffset ExpiresAt)
{
    /// <summary>Omits the code and address, so formatting or logging the record never discloses them.</summary>
    public override string ToString() => nameof(IssuedPasscode);
}

/// <summary>
/// Passwordless sign-in with six-digit email passcodes. Each user has one current code: it expires after
/// <see cref="Lifetime"/>, works once, and dies after <see cref="MaxAttempts"/> wrong guesses; at most
/// <see cref="MaxIssuesPerWindow"/> codes are issued per user per <see cref="IssueWindow"/>. Only an HMAC of the code,
/// keyed from the master key and bound to its instance and user, is stored, so a database copy alone cannot be
/// brute-forced offline. A guess at a live code is answered only after the conditional write that charges it (or
/// consumes the code) wins, so concurrent guesses cannot exceed the attempt limit and concurrent redemptions have one
/// winner. Callers authorize the instance, deliver the
/// code, and answer every request and failure identically whether or not the email has an account.
/// </summary>
public sealed class Passcodes(AuthStore store, Users users, Sessions sessions, TimeProvider clock, AuthOptions options)
{
    /// <summary>Wrong guesses a code survives.</summary>
    public const int MaxAttempts = 5;

    /// <summary>Codes issued per user per <see cref="IssueWindow"/>.</summary>
    public const int MaxIssuesPerWindow = 5;

    /// <summary>How long a code works.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The issue-limit window.</summary>
    public static readonly TimeSpan IssueWindow = TimeSpan.FromHours(1);

    private readonly byte[] digestKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth passcode digests"u8.ToArray());

    /// <summary>
    /// Issues a code that replaces the user's previous one. Returns <see langword="null"/> when no account owns the
    /// email, the user's issue limit is reached, or a concurrent request issued the code instead.
    /// </summary>
    public async Task<IssuedPasscode?> RequestAsync(string instanceId, string email, CancellationToken cancellationToken = default)
    {
        if (await users.FindByEmailAsync(instanceId, email, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var document = await store.GetAsync(AuthStore.PasscodeCollection, instanceId, user.Id, cancellationToken).ConfigureAwait(false);
        var previous = document?.Document.Deserialize(IdentityJson.Default.PasscodeRecord);
        var window = previous is not null && previous.WindowStartedAt + IssueWindow > now ? previous : null;
        if (window?.IssuedInWindow >= MaxIssuesPerWindow)
        {
            return null;
        }

        var code = Tokens.RandomDigits(6)!;
        var passcode = new PasscodeRecord(instanceId, user.Id, user.Email, this.Digest(instanceId, user.Id, code), user.AuthEpoch, now + Lifetime,
            FailedAttempts: 0, Consumed: false, window?.WindowStartedAt ?? now, (window?.IssuedInWindow ?? 0) + 1);
        var record = JsonSerializer.SerializeToElement(passcode, IdentityJson.Default.PasscodeRecord);
        bool written;
        if (document is null)
        {
            try
            {
                await store.CreateAsync(AuthStore.PasscodeCollection, instanceId, user.Id, record, cancellationToken).ConfigureAwait(false);
                written = true;
            }
            catch (TerraBasePreconditionFailedException error) when (error.ErrorCode == "precondition_failed")
            {
                written = false;
            }
        }
        else
        {
            written = await store.CompareAndPutAsync(AuthStore.PasscodeCollection, instanceId, user.Id, record, document.Version, cancellationToken).ConfigureAwait(false);
        }

        return written ? new IssuedPasscode(user.Email, code, passcode.ExpiresAt) : null;
    }

    /// <summary>
    /// Redeems a code and opens a session. Returns <see langword="null"/> for an unknown email and for a wrong,
    /// expired, used, exhausted or superseded code alike. The code is consumed before the session is written, so a
    /// failed session write costs the user a new code and never grants a second session.
    /// </summary>
    public async Task<IssuedSession?> VerifyAsync(string instanceId, string email, string code, CancellationToken cancellationToken = default)
    {
        code = code?.Trim() ?? string.Empty;
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)
            || await users.FindByEmailAsync(instanceId, email, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            return null;
        }

        var digest = this.Digest(instanceId, user.Id, code);
        // A round loses its write only to another charged guess, a redemption or a reissue, each bounded, so these
        // rounds reach a verdict under any realistic contention. Running out reveals nothing and charges nothing.
        for (var round = 0; round < MaxAttempts + 2; round++)
        {
            var document = await store.GetAsync(AuthStore.PasscodeCollection, instanceId, user.Id, cancellationToken).ConfigureAwait(false);
            var passcode = document?.Document.Deserialize(IdentityJson.Default.PasscodeRecord);
            if (passcode is null || passcode.Consumed || passcode.FailedAttempts >= MaxAttempts || passcode.ExpiresAt <= clock.GetUtcNow()
                || passcode.InstanceId != instanceId || passcode.UserId != user.Id || passcode.Email != user.Email || passcode.AuthEpoch != user.AuthEpoch)
            {
                return null;
            }

            var matches = Tokens.FixedTimeEquals(passcode.CodeHash, digest);
            var charged = matches ? passcode with { Consumed = true } : passcode with { FailedAttempts = passcode.FailedAttempts + 1 };
            if (!await store.CompareAndPutAsync(AuthStore.PasscodeCollection, instanceId, user.Id,
                    JsonSerializer.SerializeToElement(charged, IdentityJson.Default.PasscodeRecord), document!.Version, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            // A write that completes after expiry grants nothing.
            return matches && passcode.ExpiresAt > clock.GetUtcNow()
                ? await sessions.CreateAsync(user, cancellationToken).ConfigureAwait(false)
                : null;
        }

        return null;
    }

    private string Digest(string instanceId, string userId, string code) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(this.digestKey,
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{instanceId.Length}:{instanceId}{userId.Length}:{userId}{code}"))));
}
