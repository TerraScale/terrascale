
namespace TerraScale.Dashboard.Features.Profile;

public sealed record AccountDeletionRow
{
    public string? OriginalVersion { get; set; }

    public required string Id { get; init; }

    public required string KeyHash { get; init; }

    public string? UserId { get; set; }

    public byte[]? Nonce { get; set; }

    public byte[]? Ciphertext { get; set; }

    public byte[]? Tag { get; set; }

    public DateTime? CompletedUntil { get; set; }

    public required DateTime CreatedAt { get; init; }

    public required string Version { get; set; }

    public string? NonceHex => Nonce is null ? null : Convert.ToHexString(Nonce);

    public string? CiphertextHex => Ciphertext is null ? null : Convert.ToHexString(Ciphertext);

    public string? TagHex => Tag is null ? null : Convert.ToHexString(Tag);
}

public sealed record DeletionCleanupRow
{
    public const string Account = "account";
    public const string Organization = "organization";
    public const string Project = "project";

    public required string Id { get; init; }

    public required string Scope { get; init; }

    public required string SubjectId { get; init; }

    public string? OrganizationId { get; init; }

    public required DateTime CreatedAt { get; init; }

    public DateTime? AcknowledgedAt { get; init; }

    public DateTime? BillingSettledAt { get; init; }

    public required bool TargetsComplete { get; init; }
}

public sealed record DeletionCleanupTargetRow
{
    public const string Project = "project";
    public const string Customer = "customer";

    public required string CleanupId { get; init; }

    public required string Kind { get; init; }

    public required string TargetId { get; init; }
}
