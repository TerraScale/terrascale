namespace TerraScale.Authentication.Contracts;

/// <summary>TOTP enrollment and recovery state for one account in <c>mfa_credentials</c>.</summary>
/// <param name="InstanceId">The owning instance.</param>
/// <param name="UserId">The owning account.</param>
/// <param name="SecretBox">Record-bound, encrypted 160-bit TOTP secret, or null when disabled.</param>
/// <param name="Enabled">Whether the pending factor has been confirmed.</param>
/// <param name="EnrollmentEpoch">The user's epoch when pending enrollment began.</param>
/// <param name="EnrollmentExpiresAt">Pending enrollment expiry, cleared on confirmation.</param>
/// <param name="LastAcceptedStep">The largest consumed TOTP time step, or -1 before confirmation.</param>
/// <param name="RecoveryHashes">SHA-256 hashes of unconsumed recovery codes.</param>
/// <param name="RecoveryExpiresAt">Expiry of the active recovery-code batch.</param>
public sealed record TotpCredentialRecord(string InstanceId, string UserId, string? SecretBox, bool Enabled,
    long EnrollmentEpoch, DateTimeOffset? EnrollmentExpiresAt, long LastAcceptedStep,
    IReadOnlyList<string> RecoveryHashes, DateTimeOffset? RecoveryExpiresAt)
{
    /// <summary>Formatting a record never discloses its credential state.</summary>
    public override string ToString() => nameof(TotpCredentialRecord);
}

/// <summary>The pending secret and provisioning URI, returned once at the start of enrollment.</summary>
/// <param name="Secret">Base32 secret for the authenticator.</param>
/// <param name="ProvisioningUri">The otpauth URI for a QR code.</param>
/// <param name="ExpiresAt">The pending enrollment expiry.</param>
public sealed record TotpEnrollment(string Secret, string ProvisioningUri, DateTimeOffset ExpiresAt)
{
    /// <summary>Formatting the result never discloses the secret.</summary>
    public override string ToString() => nameof(TotpEnrollment);
}

/// <param name="Codes">Eight single-use recovery codes.</param>
/// <param name="ExpiresAt">The recovery-code batch expiry.</param>
public sealed record IssuedRecoveryCodes(UserRecord User, IReadOnlyList<string> Codes, DateTimeOffset ExpiresAt)
{
    /// <summary>Formatting the result never discloses recovery codes.</summary>
    public override string ToString() => nameof(IssuedRecoveryCodes);
}

/// <summary>One enrolled passkey in <c>passkey_credentials</c>.</summary>
/// <param name="CredentialId">Base64url WebAuthn credential id, globally unique per instance.</param>
/// <param name="PublicKeyCose">The COSE public key the authenticator attested.</param>
/// <param name="SignCount">The signature counter at enrollment.</param>
/// <param name="Transports">Transports the authenticator reported.</param>
/// <param name="Name">Caller-chosen label.</param>
/// <param name="Aaguid">Authenticator model identifier, display only.</param>
/// <param name="CreatedAt">Enrollment time.</param>
/// <param name="LastUsedAt">Last successful assertion, or null before the first one.</param>
public sealed record PasskeyCredentialEntry(string CredentialId, byte[] PublicKeyCose, uint SignCount, IReadOnlyList<string> Transports,
    string? Name, string Aaguid, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt)
{
    public string InstanceId { get; init; } = string.Empty;

    public string UserId { get; init; } = string.Empty;

    public override string ToString() => nameof(PasskeyCredentialEntry);
}
