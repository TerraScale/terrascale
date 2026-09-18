using System.Collections.Immutable;

namespace TerraScale.Dashboard2;

/// <summary>Admin-only inventory, estate facts, and product actions. Backed by
/// the shared seeded store; mirrors the admin API views from
/// src/features/admin/admin-data.ts and admin-estate-dashboard.ts.</summary>
public sealed partial class DashboardStore
{
    public sealed record AdminUserRow(
        string UserId,
        string DisplayName,
        string Email,
        bool Enabled,
        bool Owner,
        string CreatedAt,
        string? SupportNote = null);

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

    public ImmutableArray<AdminUserRow> AdminUsers { get; private set; } =
    [
        new("usr_01JC9Q7T4N8Y2M3P5R7V9X1ZAB", "Mario Kraus", "mario@terrascale.tech", true, true, "2026-01-12T09:24:00Z", "Org owner. Direct line."),
        new("usr_01JC9R8U5P0W3Y5A7C9E2G5IK", "Ana Souza", "ana@terrascale.tech", true, false, "2026-02-04T11:02:00Z"),
        new("usr_01JC9S9V6Q1X4Z6B8D0F3H6JN", "Ravi Patel", "ravi@terrascale.tech", true, false, "2026-02-27T15:41:00Z"),
        new("usr_01JC9TA1Z3C5E7G9I1K4M7P0S", "Dana White", "dana@northwind.io", false, true, "2025-11-18T08:10:00Z", "Billing dispute open."),
        new("usr_01JC9TB2D4F6H8J0L2N5P8S1V", "Igor Ivanov", "igor@northwind.io", true, false, "2026-03-08T17:26:00Z"),
    ];

    public ImmutableArray<AdminOrganizationRow> AdminOrganizations { get; private set; } =
    [
        new("org_01JC9Q5V2N4P6T8R0X2Z4B6D8", "TerraScale Labs", "Active", "2026-01-10T14:00:00Z", 3, 1, "Healthy"),
        new("org_01JCA2K4M6P8R0T3W5Y8A1C4E", "Northwind", "Active", "2025-11-15T09:30:00Z", 2, 1, "Dunning"),
        new("org_01JCA3L5N7Q9S1U4X6Z9B2D5F", "Helios Research", "Suspended", "2025-09-02T12:00:00Z", 1, 0, "Suspended"),
    ];

    public ImmutableArray<AdminPlanRow> AdminPlans { get; private set; } =
    [
        new("plan_free", "free", "Free", 0, "USD", true, 2, null, (10, 5_000_000_000, 2, 5, 1)),
        new("plan_scale", "scale", "Scale", 2900, "USD", true, 16, 12, (200, 200_000_000_000, 16, 50, 10)),
        new("plan_dedicated", "dedicated", "Dedicated", 19900, "USD", true, 128, 40, (2000, 2_000_000_000_000, 128, 500, 100)),
        new("plan_legacy_v1", "legacy_v1", "Legacy v1", 1900, "USD", false, 8, null, (100, 50_000_000_000, 8, 20, 4)),
    ];

    public AdminPlatformUsage AdminUsage { get; } = new(
        Organizations: 3,
        Projects: 6,
        Databases: 4,
        Usage:
        [
            new("Documents stored", 3_880_870, "documents"),
            new("Storage consumed", 6_656, "MB"),
            new("API requests (30d)", 4_812_640, "requests"),
            new("Read units (30d)", 31_204_500, "units"),
            new("Write units (30d)", 8_140_200, "units"),
            new("Active webhooks", 3, "endpoints"),
            new("MRR", 61_600, "cents"),
        ]);

    public ImmutableArray<AdminEstateActionRecord> AdminEstateActionsLog { get; private set; } = [];

    /// <summary>Estate dashboard facts keyed by section; sections with no
    /// entry render the original "No data" empty state.</summary>
    public ImmutableDictionary<string, ImmutableArray<AdminEstateFact>> AdminEstateFacts { get; } =
        new Dictionary<string, AdminEstateFact[]>
        {
            ["regions"] =
            [
                new("Available", "4 / 5", "eu-west-1 degraded"),
                new("Largest", "us-east-1", "2 databases"),
                new("Newest", "sa-east-1", "live since 2026-06"),
            ],
            ["hosts"] =
            [
                new("Placed databases", "4", "across 3 regions"),
                new("Provisioning", "1", "sandbox in sa-east-1"),
                new("Orphaned", "0"),
            ],
            ["health"] =
            [
                new("Checks passing", "47 / 48", "eu-west-1 disk watermark"),
                new("Last sweep", "2026-09-18T06:00:00Z"),
            ],
            ["clock"] =
            [
                new("Max skew", "38ms", "eu-west-1"),
                new("NTP sources", "4"),
            ],
            ["disk"] =
            [
                new("Capacity", "61%", "18.4 TiB of 30 TiB"),
                new("High watermark", "72%", "alerts at 80%"),
            ],
            ["version"] =
            [
                new("Rolling out", "2026.9.2", "3 of 11 hosts"),
                new("Stable", "2026.9.1"),
            ],
            ["placement"] =
            [
                new("Balanced", "true", "spread policy"),
                new("Pending moves", "0"),
            ],
            ["lag"] =
            [
                new("p50", "6ms"),
                new("p99", "84ms", "eu-west-1 → eu-central-1"),
            ],
            ["slo"] =
            [
                new("Availability", "99.982%", "30-day trailing"),
                new("Error budget", "71%", "remaining"),
            ],
            ["tenants"] =
            [
                new("Organizations", "3"),
                new("Suspended", "1", "Helios Research"),
            ],
            ["usage"] =
            [
                new("Requests (24h)", "161_204", "+8.4% week over week"),
                new("Top tenant", "TerraScale Labs", "63% of traffic"),
            ],
            ["revenue"] =
            [
                new("MRR", "$616.00", "2 paying plans"),
                new("Projected", "$702.00", "cycle closes Oct 1"),
            ],
            ["incidents"] =
            [
                new("Open", "1", "eu-west-1 degraded since 09-17"),
                new("MTTR (90d)", "22m"),
            ],
        }.ToImmutableDictionary(k => k.Key, k => k.Value.ToImmutableArray());

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

    public void AdminSetUserEnabled(string userId, bool enabled) =>
        AdminUsers = [.. AdminUsers.Select(u => u.UserId == userId ? u with { Enabled = enabled } : u)];

    public void AdminSetUserRole(string userId, string role) =>
        AdminUsers = [.. AdminUsers.Select(u => u.UserId == userId ? u with { Owner = role is "owner" } : u)];

    public void AdminAddSupportNote(string userId, string note) =>
        AdminUsers = [.. AdminUsers.Select(u => u.UserId == userId ? u with { SupportNote = note } : u)];

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
