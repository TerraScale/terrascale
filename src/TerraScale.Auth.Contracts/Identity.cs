namespace TerraScale.Auth.Contracts;

/// <summary>A user account (collection <c>users</c>). One account owns each normalized email per instance.</summary>
/// <param name="Id">Random UUID.</param>
/// <param name="InstanceId">The owning instance.</param>
/// <param name="Email">Trimmed, lowercased email.</param>
/// <param name="AuthEpoch">Advancing it invalidates every session and passcode issued before.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record UserRecord(string Id, string InstanceId, string Email, long AuthEpoch, DateTimeOffset CreatedAt);

/// <summary>A browser session (collection <c>sessions</c>). Only the SHA-256 of its bearer token is stored.</summary>
/// <param name="InstanceId">The owning instance.</param>
/// <param name="UserId">The signed-in user.</param>
/// <param name="TokenHash">Lowercase hex SHA-256 of the bearer token; also the document address.</param>
/// <param name="AuthEpoch">The user's epoch at authentication; the session dies when it advances.</param>
/// <param name="CreatedAt">Issue time.</param>
/// <param name="ExpiresAt">Absolute expiry, set at authentication; rotation keeps it.</param>
/// <param name="Revoked">Set by sign-out and rotation; never cleared.</param>
public sealed record SessionRecord(string InstanceId, string UserId, string TokenHash, long AuthEpoch, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool Revoked = false);

/// <summary>A user's current email passcode (collection <c>passcodes</c>, one per user).</summary>
/// <param name="InstanceId">The owning instance.</param>
/// <param name="UserId">The user it signs in.</param>
/// <param name="Email">The address it was sent to.</param>
/// <param name="CodeHash">Lowercase hex HMAC-SHA256 of the code, keyed from the master key and bound to instance and user.</param>
/// <param name="AuthEpoch">The user's epoch at issue.</param>
/// <param name="ExpiresAt">Expiry of this code.</param>
/// <param name="FailedAttempts">Wrong guesses charged against this code.</param>
/// <param name="Consumed">Set once the code is redeemed.</param>
/// <param name="WindowStartedAt">Start of the current issue-limit window.</param>
/// <param name="IssuedInWindow">Codes issued in that window.</param>
public sealed record PasscodeRecord(
    string InstanceId,
    string UserId,
    string Email,
    string CodeHash,
    long AuthEpoch,
    DateTimeOffset ExpiresAt,
    int FailedAttempts,
    bool Consumed,
    DateTimeOffset WindowStartedAt,
    int IssuedInWindow);
