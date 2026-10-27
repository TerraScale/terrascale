using System.Text.Json.Serialization;

namespace TerraScale.Authentication.Contracts;

/// <summary>A user account (collection <c>users</c>). One account owns each normalized email per instance.</summary>
/// <param name="Id">Canonical ULID.</param>
/// <param name="InstanceId">The owning instance.</param>
/// <param name="Email">Trimmed, lowercased email.</param>
/// <param name="AuthEpoch">Advancing it invalidates every session and passcode issued before.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="PasswordHash">Argon2id PHC string, or <see langword="null"/> when the account has no password.</param>
/// <param name="PlatformRole">
/// <c>admin</c> for a TerraScale platform admin of the <c>terrascale</c> instance, who may explore the platform databases
/// in TerraBase; <see langword="null"/> otherwise. No API sets it: an operator writes it to the user's document through
/// TerraBase's internal listener. The platform owner is an admin without it (ts-auth's PlatformBootstrap.OwnerEmail).
/// </param>
public sealed record UserRecord(string Id, string InstanceId, string Email, long AuthEpoch, DateTimeOffset CreatedAt, string? PasswordHash = null, string? PlatformRole = null)
{
    /// <summary>Identity of the last authentication-state change; equal numeric epochs can race across regions.</summary>
    public string? AuthStamp { get; init; }

    /// <summary>A disabled user cannot sign in; disabling advances the epoch, which ends its sessions and tokens.</summary>
    public bool Disabled { get; init; }

    /// <summary>Verified external identities linked to this account; provider subjects never serve as user ids.</summary>
    public IReadOnlyList<LinkedProvider> LinkedProviders { get; init; } = [];

    /// <summary>Proof lineage for this authentication attempt; never written back to the account.</summary>
    [JsonIgnore]
    public IReadOnlyList<CredentialBinding> AuthenticationBindings { get; init; } = [];

    /// <summary>Fresh primary proof ancestry for a newly issued secret; excludes the caller's browser authorization.</summary>
    [JsonIgnore]
    public IReadOnlyList<CredentialBinding> StepUpBindings { get; init; } = [];

    /// <summary>Whether this authentication attempt proved the account's second factor; never written back to the account.</summary>
    [JsonIgnore]
    public bool MultiFactor { get; init; }
}

/// <summary>A browser session (collection <c>sessions</c>). Only the SHA-256 of its bearer token is stored.</summary>
/// <param name="InstanceId">The owning instance.</param>
/// <param name="UserId">The signed-in user.</param>
/// <param name="TokenHash">Lowercase hex SHA-256 of the bearer token; also the document address.</param>
/// <param name="AuthEpoch">The user's epoch at authentication; the session dies when it advances.</param>
/// <param name="CreatedAt">Issue time.</param>
/// <param name="ExpiresAt">Absolute expiry, set at authentication; rotation keeps it.</param>
/// <param name="Revoked">Set by sign-out and rotation; never cleared.</param>
public sealed record SessionRecord(string InstanceId, string UserId, string TokenHash, long AuthEpoch, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool Revoked = false)
{
    /// <summary>The user's authentication-state identity when this session was opened.</summary>
    public string? AuthStamp { get; init; }

    /// <summary>Every one-use ancestor must still name this branch as its winner.</summary>
    public IReadOnlyList<CredentialBinding> Bindings { get; init; } = [];

    /// <summary>Whether the sign-in that opened this session proved the account's second factor.</summary>
    public bool Mfa { get; init; }
}

/// <summary>
/// A user's current email passcode (collection <c>passcodes</c>, one per user), or a pending sign-up's code (one per
/// email, before the account exists).
/// </summary>
/// <param name="InstanceId">The owning instance.</param>
/// <param name="UserId">The user it signs in; for a sign-up, a random id that only binds the code.</param>
/// <param name="Email">The address it was sent to.</param>
/// <param name="CodeHash">Lowercase hex HMAC-SHA256 of the code, keyed from the master key and bound to instance and user.</param>
/// <param name="AuthEpoch">The user's epoch at issue.</param>
/// <param name="ExpiresAt">Expiry of this code.</param>
/// <param name="FailedAttempts">Wrong guesses charged against this code.</param>
/// <param name="Consumed">Set once the code is redeemed.</param>
/// <param name="WindowStartedAt">Timestamp identifying the current UTC clock-hour issue-limit window.</param>
/// <param name="IssuedInWindow">Codes issued in that window.</param>
/// <param name="Purpose"><c>password-reset</c>, <c>sign-up</c> or <c>step-up</c>; <see langword="null"/> for a sign-in code.</param>
/// <param name="PasswordHash">A pending sign-up's Argon2id hash, cleared when the code is redeemed.</param>
/// <param name="PendingTokenHash">
/// Lowercase hex SHA-256 of the pending token handed to whoever started the sign-up; only that requester can redeem
/// the code, so a third party cannot attach their password to someone else's address. For step-up, the digest of the
/// request id binds the code to exactly that request instead.
/// </param>
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
    int IssuedInWindow,
    string? Purpose = null,
    string? PasswordHash = null,
    string? PendingTokenHash = null)
{
    /// <summary>The authentication-state identity at issue.</summary>
    public string? AuthStamp { get; init; }

    /// <summary>Identifies this issuance even if a later random code repeats.</summary>
    public string IssueId { get; init; } = string.Empty;

    /// <summary>Immutable issuance predecessors, retained when a later code replaces this one.</summary>
    public IReadOnlyList<CredentialBinding> IssuanceBindings { get; init; } = [];

    /// <summary>Redemption verifies fresh email possession and advances authentication state before starting a new family.</summary>
    public bool ResetsAuthentication { get; init; }

    /// <summary>Pruning removes code material but retains the issuance anchor.</summary>
    public bool Pruned { get; init; }

    /// <summary>The live code this one replaced, still redeemable until <see cref="PreviousExpiresAt"/>, so a slow email works after a resend.</summary>
    public string? PreviousCodeHash { get; init; }

    /// <summary>Expiry of <see cref="PreviousCodeHash"/>.</summary>
    public DateTimeOffset? PreviousExpiresAt { get; init; }

    /// <summary>Dedicated step-up request id; its passcode never inherits sign-in code ancestry or resets authentication.</summary>
    public string? StepUpRequestId { get; init; }

    /// <summary>The winning redemption this result depends on; not part of the stored passcode.</summary>
    [JsonIgnore]
    public CredentialBinding? Binding { get; init; }

    /// <summary>The new state selected by an atomic reset or issuance-budget recovery.</summary>
    [JsonIgnore]
    public UserRecord? AuthenticatedUser { get; init; }
}

/// <summary>A branch of an immutable, tenant-scoped one-use claim. Identifiers contain no bearer secrets.</summary>
/// <param name="Kind">The credential claim namespace.</param>
/// <param name="Key">The consumed credential's issuance identifier.</param>
/// <param name="OwnerId">The request branch that must win the replicated claim.</param>
public sealed record CredentialBinding(string Kind, string Key, string OwnerId);

/// <summary>A provider identity, scoped to the tenant and its OAuth client.</summary>
/// <param name="Provider">Provider name.</param>
/// <param name="ClientId">OAuth client that verified this subject.</param>
/// <param name="Subject">Stable provider subject.</param>
public sealed record LinkedProvider(string Provider, string ClientId, string Subject);
