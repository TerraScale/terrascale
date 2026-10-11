namespace TerraScale.Auth.Contracts;

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

/// <summary>The changed user and its plaintext recovery codes, returned once at activation.</summary>
/// <param name="User">The account with its advanced authentication epoch.</param>
/// <param name="Codes">Eight single-use recovery codes.</param>
/// <param name="ExpiresAt">The recovery-code batch expiry.</param>
public sealed record IssuedRecoveryCodes(UserRecord User, IReadOnlyList<string> Codes, DateTimeOffset ExpiresAt)
{
    /// <summary>Formatting the result never discloses recovery codes.</summary>
    public override string ToString() => nameof(IssuedRecoveryCodes);
}
