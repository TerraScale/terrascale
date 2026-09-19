using System.Collections.Immutable;

namespace TerraScale.Dashboard2;

/// <summary>Admin-only inventory, estate facts, and product actions. Mirrors
/// the admin API views from src/features/admin/admin-data.ts and
/// admin-estate-dashboard.ts. Inventory and usage are account-world state;
/// the plan catalog is platform-wide.</summary>
public sealed partial class DashboardStore
{
    public sealed record AdminUserRow(
        string UserId,
        string DisplayName,
        string Email,
        bool Enabled,
        bool Owner,
        string CreatedAt);

    public sealed record AdminOrganizationRow(
        string OrganizationId,
        string Name,
        string Status,
        string CreatedAt,
        int ProjectCount,
        int OwnerCount,
        string BillingPosture);   // Healthy | Dunning | Trial | Suspended

    public sealed record AdminPlanRow(
        string PlanId,
        string Key,
        string Name,
        long MonthlyBaseCents,
        string Currency,
        bool Visible,
        int MaxDatabases,
        long? StorageOverageCentsPerGbRegion,
        (int ReadQps, long DirectReadBandwidthBytesPerMonth, int ActiveViews, int WebsocketConnections, int WebhookEndpoints)? Guards);

    public sealed record AdminEstateFact(string Key, string Value, string? Detail = null);

    public sealed record AdminUsageMetric(string Name, long Quantity, string? Unit);

    public sealed record AdminPlatformUsage(
        int Organizations,
        int Projects,
        int Databases,
        ImmutableArray<AdminUsageMetric> Usage);

    public sealed record AdminEstateActionRecord(
        string Action,
        string TargetId,
        string Reason,
        string ActorEmail,
        string OccurredAt);

    /// <summary>Admin user inventory: the demo platform roster, or the live
    /// registered accounts for a fresh admin session (v1's admin API lists
    /// exactly the registered users).</summary>
    public ImmutableArray<AdminUserRow> AdminUsers => World.IsDemo ? World.AdminUsers : RegisteredAdminUsers;

    public ImmutableArray<AdminOrganizationRow> AdminOrganizations { get => World.AdminOrganizations; private set => World.AdminOrganizations = value; }

    /// <summary>Seeded billing plan catalog — mirrors src/backend/billing/catalog.ts
    /// (createSeedPlans), ordered by name as the admin API returns it.</summary>
    public ImmutableArray<AdminPlanRow> AdminPlans { get; private set; } =
    [
        new("01J0000000H8N2R4T6V9WXBQZD", "business", "Business", 9_900, "USD", true, 100, 20, (10_000, 20_000_000_000_000, 1_000_000, 100_000, 200)),
        new("01J0000000ENTERPRISE000000", "enterprise", "Enterprise", 0, "USD", false, -1, 20, (-1, -1, -1, -1, -1)),
        new("01J0000000QHGCSDT0B5HTTNK2", "free", "Free", 0, "USD", true, 1, 20, (100, 50_000_000_000, 1_000, 100, 3)),
        new("01J0000000CVGZW8WBHBVYKDZ6", "pro", "Pro", 5_000, "USD", true, 25, 20, (5_000, 2_000_000_000_000, 100_000, 10_000, 50)),
        new("01J0000000SXC54B5QZTXDXQYT", "scale", "Scale", 50_000, "USD", true, -1, 20, (-1, -1, -1, -1, -1)),
        new("01J00000004WM5Z7P5MDBPN77M", "starter", "Starter", 500, "USD", true, 5, 20, (1_000, 500_000_000_000, 10_000, 1_000, 10)),
    ];

    public AdminPlatformUsage AdminUsage => World.AdminUsage;

    public ImmutableArray<AdminEstateActionRecord> AdminEstateActionsLog { get => World.AdminEstateActionsLog; private set => World.AdminEstateActionsLog = value; }

    /// <summary>Estate dashboard facts keyed by section; sections with no
    /// entry render the original "No data" empty state.</summary>
    public ImmutableDictionary<string, ImmutableArray<AdminEstateFact>> AdminEstateFacts => World.AdminEstateFacts;

    public static readonly ImmutableArray<(string Key, string Title)> EstateSections =
    [
        ("regions", "Regions"), ("hosts", "Hosts"), ("health", "Health"), ("clock", "Clock"),
        ("disk", "Disk"), ("version", "Version"), ("placement", "Placement"), ("lag", "Replication lag"),
        ("queues", "Queues"), ("rbsr", "RBSR"), ("slo", "SLO"), ("rollout", "Rollout"),
        ("drift", "Drift"), ("kv", "KV"), ("cache", "Cache"), ("tenants", "Tenants"),
        ("usage", "Usage"), ("revenue", "Revenue"), ("unitEconomics", "Unit economics"),
        ("indexes", "Indexes"), ("drill", "Drill-down"), ("guards", "Guards"), ("incidents", "Incidents"),
    ];

    public static string EstateActionNotice => "Action completed and recorded in the audit trail.";

    // --- Mutations ---------------------------------------------------------

    public void AdminSetPlanVisibility(string planId, bool visible) =>
        AdminPlans = [.. AdminPlans.Select(p => p.PlanId == planId ? p with { Visible = visible } : p)];

    public void AdminDeletePlan(string planId) =>
        AdminPlans = [.. AdminPlans.Where(p => p.PlanId != planId)];

    public void AdminSavePlan(AdminPlanRow plan, bool editing)
    {
        if (editing && AdminPlans.Any(p => p.PlanId == plan.PlanId))
        {
            AdminPlans = [.. AdminPlans.Select(p => p.PlanId == plan.PlanId ? plan : p)];
            return;
        }

        AdminPlans = AdminPlans.Add(plan with { PlanId = $"plan_{plan.Key}" });
    }

    public void AdminRecordEstateAction(string action, string targetId, string reason, string actorEmail) =>
        AdminEstateActionsLog = AdminEstateActionsLog.Add(
            new AdminEstateActionRecord(action, targetId, reason, actorEmail, DateTimeOffset.Now.ToString("O")));
}
