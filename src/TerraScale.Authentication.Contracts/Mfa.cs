namespace TerraScale.Authentication.Contracts;

public sealed record TotpCredentialRecord(string InstanceId, string UserId, string? SecretBox, bool Enabled,
    long EnrollmentEpoch, DateTimeOffset? EnrollmentExpiresAt, long LastAcceptedStep,
    IReadOnlyList<string> RecoveryHashes, DateTimeOffset? RecoveryExpiresAt)
{
    public string? Version { get; init; }

    public override string ToString() => nameof(TotpCredentialRecord);
}

public sealed record TotpEnrollment(string Secret, string ProvisioningUri, DateTimeOffset ExpiresAt)
{
    public override string ToString() => nameof(TotpEnrollment);
}

public sealed record IssuedRecoveryCodes(UserRecord User, IReadOnlyList<string> Codes, DateTimeOffset ExpiresAt)
{
    public override string ToString() => nameof(IssuedRecoveryCodes);
}

public sealed record PasskeyCredentialEntry(string CredentialId, byte[] PublicKeyCose, uint SignCount, IReadOnlyList<string> Transports,
    string? Name, string Aaguid, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt)
{
    public string? Version { get; init; }

    public string InstanceId { get; init; } = string.Empty;

    public string UserId { get; init; } = string.Empty;

    public override string ToString() => nameof(PasskeyCredentialEntry);
}
