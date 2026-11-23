
namespace TerraScale.Dashboard.Common.Accounts;

public sealed record OrganizationRow
{
    public string? OriginalVersion { get; set; }

    public required string Id { get; init; }

    public required string Kind { get; set; }

    public required string Status { get; set; }

    public required string PlanId { get; set; }

    public required string BillingStatus { get; set; }

    public DateTime? PaymentGraceUntil { get; set; }

    public required bool PaymentMethodValidated { get; set; }

    public required long SpendCapCents { get; set; }

    public required bool PaidExcess { get; set; }

    public required bool Archived { get; set; }

    public required bool DeletionRequested { get; set; }

    public string? InternalBilling { get; set; }

    public required string InternalBillingAlerts { get; set; }

    public int? AuditRetentionDays { get; set; }

    public required string PendingProjectDeletions { get; set; }

    public required string DeletedProjectIds { get; set; }

    public string? ErasureId { get; set; }

    public required string Version { get; set; }
}

public sealed record OrganizationProjectRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string Regions { get; set; }

    public required string Status { get; set; }

    public required bool IsDefault { get; set; }

    public required long SpendCapCents { get; set; }

    public required long ReadLimit { get; set; }

    public required long WriteLimit { get; set; }

    public required long ReplicationLimit { get; set; }
}

public sealed record ProjectDatabaseRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string ProjectId { get; set; }

    public required string Name { get; set; }

    public string? PreviousName { get; set; }

    public string? ConflictOf { get; set; }

    public required string Region { get; set; }

    public required string Regions { get; set; }

    public required bool AllRegions { get; set; }

    public required long PlacementRevision { get; set; }

    public required string Endpoint { get; set; }

    public required string Status { get; set; }

    public required long SpendCapCents { get; set; }

    public required long ReadLimit { get; set; }

    public required long WriteLimit { get; set; }

    public required long ReplicationLimit { get; set; }

    public required int BackupRetentionDays { get; set; }

    public string? BranchedFrom { get; set; }

    public required DateTime CreatedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }
}

public sealed record AuditEventRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string Actor { get; set; }

    public required string Action { get; set; }

    public required string Target { get; set; }

    public required string TargetType { get; set; }

    public required DateTime OccurredAt { get; set; }

    public required string Source { get; set; }

    public string? Risk { get; set; }

    public string? Ip { get; set; }
}

public sealed record InvitationReservationRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string Email { get; set; }

    public required DateTime ReservedAt { get; set; }

    public required string InvitationId { get; set; }

    public string? ProjectId { get; set; }

    public required string Known { get; set; }
}
