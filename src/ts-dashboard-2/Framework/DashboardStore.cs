using System.Collections.Immutable;

namespace TerraScale.Dashboard2;

/// <summary>Seeded in-memory demo data powering every page.</summary>
public sealed partial class DashboardStore
{
    public DashboardUser CurrentUser { get; private set; } = new(
        Id: "usr_01JC9Q7T4N8Y2M3P5R7V9X1ZAB",
        Name: "Mario Kraus",
        Email: "mario@terrascale.tech",
        Role: "owner",
        Status: "Active",
        MfaEnabled: true,
        AvatarInitials: "MK",
        Provider: "password");

    public string ActiveOrganizationId => "org_01JC9Q5V2N4P6T8R0X2Z4B6D8";
    public string ActiveOrganizationName { get; private set; } = "TerraScale Labs";
    public bool OnboardingCompleted => true;
    public bool OwnerSession => true;

    public ImmutableArray<DashboardProject> Projects { get; private set; } =
    [
        new("prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "org_01JC9Q5V2N4P6T8R0X2Z4B6D8", "core-platform", "us-east-1", "2026-01-12T09:24:00Z", IsDefault: true),
        new("prj_01JC9R2X4P6T8V1Z3B5D7F9HC", "org_01JC9Q5V2N4P6T8R0X2Z4B6D8", "edge-analytics", "eu-central-1", "2026-02-03T14:02:00Z"),
        new("prj_01JC9S4Y6R8T0V2X4Z6B8D1FE", "org_01JC9Q5V2N4P6T8R0X2Z4B6D8", "staging-sandbox", "sa-east-1", "2026-03-21T11:47:00Z"),
    ];

    public string ActiveProjectId { get; private set; } = "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA";

    public ImmutableArray<DashboardDatabase> Databases { get; private set; } =
    [
        new("db_01JC9Q8Y5N7P9R3T5V7X9Z2AC", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "core-primary", "Active", "us-east-1", "us-west-2", 4096, 128_450, "2026-01-12T09:31:00Z"),
        new("db_01JC9R3Z6P8T0V4X6Z8B2D4GE", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "core-sessions", "Active", "us-east-1", null, 512, 812_300, "2026-01-14T10:02:00Z"),
        new("db_01JC9S5A7R9V1X3Z5B7D9F3HI", "prj_01JC9R2X4P6T8V1Z3B5D7F9HC", "edge-events", "Active", "eu-central-1", "eu-west-1", 2048, 2_940_120, "2026-02-03T14:11:00Z"),
        new("db_01JC9T6B8S0X2Z4B6D8F1H3JK", "prj_01JC9S4Y6R8T0V2X4Z6B8D1FE", "sandbox", "Provisioning", "sa-east-1", null, 0, 0, "2026-09-17T08:15:00Z"),
    ];

    public ImmutableArray<ProjectApiKey> ApiKeys { get; private set; } =
    [
        new("key_01JC9Q9Z6N8P1R4T7V0X3Z6B9", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "ci-deploy", "tsk_live_9f2", "4c1a", ["databases:read", "documents:write"], "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC", "mario@terrascale.tech", "2026-02-10T12:00:00Z", "2026-12-10T12:00:00Z", "2026-09-17T06:41:00Z", null),
        new("key_01JC9R4A8P0T3V6X9Z2C5E8HB", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "local-dev", "tsk_live_2b7", "8e3d", ["databases:read"], null, "mario@terrascale.tech", "2026-03-02T09:15:00Z", null, "2026-09-16T22:03:00Z", null),
        new("key_01JC9S6B9R1X4Z7B0D3F6J9LM", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "retired-key", "tsk_live_7d5", "0f92", ["*"], null, "mario@terrascale.tech", "2025-11-04T16:20:00Z", "2026-05-04T16:20:00Z", "2026-04-28T10:00:00Z", "2026-05-01T08:00:00Z"),
    ];

    public ImmutableArray<M2mApplication> M2mApplications { get; private set; } =
    [
        new("m2m_01JC9Q8A2C4E6G8J0L2N4Q7ST", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "ingest-worker", "Active", "2026-02-18T08:00:00Z", "2026-09-18T04:12:00Z"),
        new("m2m_01JC9R9B3D5F7H9K1M3P5S8UV", "prj_01JC9R2X4P6T8V1Z3B5D7F9HC", "edge-sync", "Active", "2026-03-05T13:30:00Z", "2026-09-18T02:55:00Z"),
    ];

    public ImmutableArray<TeamMember> TeamMembers { get; private set; } =
    [
        new("usr_01JC9Q7T4N8Y2M3P5R7V9X1ZAB", "Mario Kraus", "mario@terrascale.tech", "owner", "Active", true, "2026-09-18T07:12:00Z", 2),
        new("usr_01JC9R8U5P0W3Y5A7C9E2G5IK", "Ana Souza", "ana@terrascale.tech", "admin", "Active", true, "2026-09-17T18:40:00Z", 1),
        new("usr_01JC9S9V6Q1X4Z6B8D0F3H6JN", "Ravi Patel", "ravi@terrascale.tech", "member", "Active", false, "2026-09-15T09:03:00Z", 0),
    ];

    public ImmutableArray<Invitation> Invitations { get; private set; } =
    [
        new("inv_01JC9TAB2S4U6W8Y1B3D5F8HK", "jules@contractor.dev", "member", "Pending", "mario@terrascale.tech", "2026-09-16T15:00:00Z", "tok_01HXINVITEA2B4C"),
        new("inv_01JC9TBC3T5V7X9Z2C4E6G9JL", "former@terrascale.tech", "member", "Expired", "ana@terrascale.tech", "2026-07-02T11:30:00Z", "tok_01HXINVITEB3C5D"),
    ];

    public ImmutableArray<Notification> Notifications { get; private set; } =
    [
        new("ntf_01JC9UBD4U6W8Y0B3D5F7J2NP", "api-key-expiry", "API key expiring soon", "ci-deploy expires in 7 days. Rotate it to avoid breaking the pipeline.", "Unread", "warning", "2026-09-17T09:00:00Z"),
        new("ntf_01JC9UCE5V7X9Z1C4E6H1M4OQ", "invitation", "Invitation pending", "jules@contractor.dev has not accepted the invitation yet.", "Unread", "info", "2026-09-16T15:05:00Z"),
        new("ntf_01JC9UDF6W8Y0D2E5F7J2N5PR", "quota", "Storage at 82%", "core-primary is using 82% of the plan storage quota.", "Read", "warning", "2026-09-14T20:30:00Z"),
        new("ntf_01JC9UEG7X9Z1E3F6G8K3P6ST", "backup", "Backup completed", "core-primary nightly backup finished in 42s.", "Read", "success", "2026-09-14T03:12:00Z"),
    ];

    public ImmutableArray<AuditEvent> AuditEvents { get; private set; } =
    [
        new("aud_01JC9VFH8Y0A2C5E8H0K4S8WX", "mario@terrascale.tech", "api_key.created", "ci-deploy", "api_key", "low", "dashboard", "2026-09-18T07:05:00Z"),
        new("aud_01JC9VGI9Z1B3D6F9I1L5T9YA", "ana@terrascale.tech", "member.role_changed", "ravi@terrascale.tech", "member", "medium", "dashboard", "2026-09-17T14:22:00Z"),
        new("aud_01JC9VHJ0B2C4E7G0J2M6U1ZB", "ci-deploy (api key)", "document.batch_upsert", "core-primary/orders", "database", "low", "api", "2026-09-17T06:41:00Z"),
        new("aud_01JC9VIK1C3D5F8H1K3N7V3AC", "mario@terrascale.tech", "database.deleted", "old-reporting", "database", "high", "dashboard", "2026-09-12T11:04:00Z"),
        new("aud_01JC9VJM2D4E6G9I2L4P8W5BD", "system", "backup.restore_completed", "core-sessions", "database", "medium", "scheduled", "2026-09-10T02:48:00Z"),
    ];

    public UsageCycle Usage { get; } = new(
        Period: "2026-09 (cycle closes Oct 1)",
        StorageMb: 6656,
        StorageLimitMb: 8192,
        Requests: 4_812_640,
        RequestLimit: 5_000_000,
        ReadUnits: 31_204_500,
        WriteUnits: 8_140_200,
        InvoiceStatus: "Pending",
        OverrunEstimateUsd: 12.40m);

    public ImmutableArray<(string Range, ImmutableArray<WorkloadPoint> Points)> Workloads { get; } =
    [
        ("24h", SeedWorkload(24, 950, 180)),
        ("7d", SeedWorkload(7 * 24, 880, 320)),
        ("30d", SeedWorkload(30, 820, 420)),
    ];

    public ImmutableArray<RegionInfo> Regions { get; } =
    [
        new("us-east-1", "Virginia, US", "Live", 12, 2),
        new("us-west-2", "Oregon, US", "Live", 28, 1),
        new("eu-central-1", "Frankfurt, DE", "Live", 74, 1),
        new("eu-west-1", "Dublin, IE", "Degraded", 96, 0),
        new("sa-east-1", "São Paulo, BR", "Live", 118, 1),
    ];

    public ImmutableArray<SavedQuery> SavedQueries { get; private set; } =
    [
        new("sq_01JC9XN4R6T8V1X4Z7B9D3F6H", "orders-over-500", "SELECT * FROM orders WHERE total > 500", "41", "2026-09-16T10:20:00Z", "ana@terrascale.tech"),
        new("sq_01JC9XP5S7U9W2Y5A8C1E5G7J", "sessions-active-7d", "SELECT id, startedAt FROM sessions WHERE startedAt > now() - 7d", "12", "2026-09-14T16:44:00Z", "mario@terrascale.tech"),
    ];

    public ImmutableArray<PricingPlan> Plans { get; private set; } =
    [
        new("plan_free", "Free", 0m, 512, 1_000_000, "Active", 14_203),
        new("plan_scale", "Scale", 29m, 8192, 5_000_000, "Active", 3_412),
        new("plan_dedicated", "Dedicated", 199m, 131_072, 50_000_000, "Active", 187),
        new("plan_legacy_v1", "Legacy v1", 19m, 2048, 2_000_000, "Deprecated", 42),
    ];

    public ImmutableArray<Session> Sessions { get; private set; } =
    [
        new("ses_01JC9ZQ7T9V2X5A8C1E4G9K2N", "Chrome on Linux", "203.0.113.24", "São Paulo, BR", "2026-09-18T07:12:00Z", "password", true),
        new("ses_01JC9ZR8U1W3Y6B9D2F5H0M4P", "ts-cli", "203.0.113.24", "São Paulo, BR", "2026-09-17T22:41:00Z", "api-key", false),
        new("ses_01JC9ZS9V2X4Z7C0E3G6J3N6Q", "Safari on macOS", "198.51.100.7", "Frankfurt, DE", "2026-09-12T09:26:00Z", "github", false),
    ];

    public ImmutableArray<WebhookEndpoint> Webhooks { get; private set; } =
    [
        new("wh_01JCA0RBD2F4H7J0M3P6S1V4Y", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "https://ops.example.net/hooks/terrascale", "Active", "2026-02-11T09:00:00Z", "whsec_••••f4a9"),
    ];

    public ImmutableArray<DashboardUser> AllUsers { get; private set; } =
    [
        new("usr_01JC9Q7T4N8Y2M3P5R7V9X1ZAB", "Mario Kraus", "mario@terrascale.tech", "owner", "Active", true, "MK", Provider: "password", SupportNote: "Org owner. Direct line.", OrganizationName: "TerraScale Labs"),
        new("usr_01JC9R8U5P0W3Y5A7C9E2G5IK", "Ana Souza", "ana@terrascale.tech", "admin", "Active", true, "AS", Provider: "github", OrganizationName: "TerraScale Labs"),
        new("usr_01JC9S9V6Q1X4Z6B8D0F3H6JN", "Ravi Patel", "ravi@terrascale.tech", "member", "Active", false, "RP", Provider: "google", OrganizationName: "TerraScale Labs"),
        new("usr_01JC9TA1Z3C5E7G9I1K4M7P0S", "Dana White", "dana@northwind.io", "owner", "Suspended", false, "DW", Provider: "sso", SupportNote: "Billing dispute open.", OrganizationName: "Northwind"),
        new("usr_01JC9TB2D4F6H8J0L2N5P8S1V", "Igor Ivanov", "igor@northwind.io", "member", "Active", true, "II", Provider: "password", OrganizationName: "Northwind"),
    ];

    // --- Queries -----------------------------------------------------------

    public DashboardProject ActiveProject => Projects.First(p => p.Id == ActiveProjectId);

    public ImmutableArray<DashboardDatabase> ProjectDatabases(string projectId) =>
        [.. Databases.Where(d => d.ProjectId == projectId)];

    public int UnreadNotifications => Notifications.Count(n => n.Status == "Unread");

    public string Initials(string name, string email)
    {
        var source = string.IsNullOrWhiteSpace(name) ? email : name;
        var parts = source.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant(),
        };
    }

    // --- Mutations ---------------------------------------------------------

    public void SwitchProject(string projectId)
    {
        if (Projects.Any(p => p.Id == projectId))
        {
            ActiveProjectId = projectId;
        }
    }

    public void CreateProject(string name, string region) =>
        Projects = Projects.Add(new($"prj_{Guid.NewGuid():N}"[..20], ActiveOrganizationId, name, region, DateTimeOffset.Now.ToString("O")));

    public void CreateDatabase(string name)
    {
        var project = ActiveProject;
        Databases = Databases.Add(new($"db_{Guid.NewGuid():N}"[..20], project.Id, name, "Provisioning", project.Region, null, 0, 0, DateTimeOffset.Now.ToString("O")));
    }

    public void DeleteDatabase(string databaseId) =>
        Databases = [.. Databases.Where(d => d.Id != databaseId)];

    public void RevokeApiKey(string keyId) =>
        ApiKeys = [.. ApiKeys.Select(k => k.KeyId == keyId ? k with { RevokedAt = DateTimeOffset.Now.ToString("O") } : k)];

    public string CreateApiKey(string name, string[] scopes, string? databaseId)
    {
        var secret = $"tsk_live_{Convert.ToHexString(Guid.NewGuid().ToByteArray())[..12].ToLowerInvariant()}";
        var key = new ProjectApiKey(
            $"key_{Guid.NewGuid():N}"[..20], ActiveProjectId, name, secret[..12], secret[^4..],
            scopes, databaseId, CurrentUser.Email, DateTimeOffset.Now.ToString("O"),
            DateTimeOffset.Now.AddMonths(6).ToString("O"), null, null);
        ApiKeys = ApiKeys.Add(key);
        return secret;
    }

    public void MarkNotificationRead(string id) =>
        Notifications = [.. Notifications.Select(n => n.Id == id ? n with { Status = "Read" } : n)];

    public void MarkAllNotificationsRead() =>
        Notifications = [.. Notifications.Select(n => n with { Status = "Read" })];

    public void InviteMember(string email, string role) =>
        Invitations = Invitations.Add(new($"inv_{Guid.NewGuid():N}"[..20], email, role, "Pending", CurrentUser.Email, DateTimeOffset.Now.ToString("O"), $"tok_{Guid.NewGuid():N}"[..16]));

    public void RevokeInvitation(string id) =>
        Invitations = [.. Invitations.Select(i => i.Id == id ? i with { Status = "Revoked" } : i)];

    public void SaveQuery(string name, string query) =>
        SavedQueries = SavedQueries.Add(new($"sq_{Guid.NewGuid():N}"[..20], name, query, "1", DateTimeOffset.Now.ToString("O"), CurrentUser.Email));

    public void DeleteSavedQuery(string id) =>
        SavedQueries = [.. SavedQueries.Where(q => q.Id != id)];

    public void CreateWebhook(string url) =>
        Webhooks = Webhooks.Add(new($"wh_{Guid.NewGuid():N}"[..20], ActiveProjectId, url, "Active", DateTimeOffset.Now.ToString("O"), "whsec_••••1c2d"));

    public void RevokeSession(string id) =>
        Sessions = [.. Sessions.Where(s => s.Id != id)];

    private static ImmutableArray<WorkloadPoint> SeedWorkload(int points, double baseValue, double variance)
    {
        var random = new Random(42);
        var result = new List<WorkloadPoint>(points);
        var value = baseValue;
        for (var i = 0; i < points; i++)
        {
            value = Math.Max(50, value + (random.NextDouble() - 0.48) * variance * 0.4);
            result.Add(new WorkloadPoint(i.ToString(), Math.Round(value)));
        }
        return [.. result];
    }
}
