namespace TerraScale.Dashboard;

/// <summary>An expiring confirmation plan for a restore (the plan half of
/// the plan -> confirm flow; BackupConfirmationPlanView in backups-data.ts).</summary>
public sealed record RestorePlan(
    string Token,
    string DatabaseId,
    string DatabaseName,
    string Generation,
    string TargetRegion,
    string RequestDigest,
    string CreatedAt,
    string ExpiresAt);
