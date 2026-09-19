using System.Collections.Immutable;
using TerraScale.Dashboard2.Features.Data;

namespace TerraScale.Dashboard2;

/// <summary>
/// One signed-in account's entire dashboard state: profile, organization,
/// project, and every feature collection. <see cref="DashboardStore"/> keeps
/// one world per session token and forwards its properties to the active
/// world, so every page and mutation is account-scoped without further wiring.
/// </summary>
public sealed class AccountWorld
{
    private AccountWorld(bool demo)
    {
        IsDemo = demo;
        CreatedAt = DateTimeOffset.UtcNow;
        if (demo)
        {
            SeedDemo();
        }
    }

    public static AccountWorld Demo() => new(demo: true);

    /// <summary>Fresh account world seeded the way ts-dashboard's e2e backend
    /// renders a just-registered member: empty collections, zero usage, the
    /// degraded monitoring/fleet states, and the seeded org invitation.</summary>
    public static AccountWorld Fresh(string displayName, string email)
    {
        var world = new AccountWorld(demo: false);
        world.SeedFresh(displayName, email);
        return world;
    }

    public bool IsDemo { get; }

    public DateTimeOffset CreatedAt { get; }

    // --- Session-scoped account state --------------------------------------

    public DashboardUser CurrentUser { get; set; } = null!;

    public string RegisteredEmail { get; set; } = "";

    public string RegisteredName { get; set; } = "";

    public string AccountPassword { get; set; } = Features.Auth.PasswordLoginEndpoint.DemoPassword;

    public string AccountLocale { get; set; } = "en";

    public bool OrganizationDeletionRequested { get; set; }

    public string ActiveOrganizationName { get; set; } = "Personal";

    public string ActiveOrganizationId { get; set; } = "";

    public bool OnboardingCompleted { get; set; }

    /// <summary>Platform-owner (admin) session; fresh accounts are plain
    /// members until promoted through the testing dev-shim, like
    /// ts-dashboard's promoteOwner.</summary>
    public bool OwnerSession { get; private set; } = true;

    public bool PromotedOwner { get; private set; }

    public void PromoteToOwner()
    {
        OwnerSession = true;
        PromotedOwner = true;
        CurrentUser = CurrentUser with { Role = "owner" };
        MonitoringStale = false;
        DatabaseListError = null;
    }

    public bool VerifyPassword(string password) => password == AccountPassword;

    public void ChangePassword(string currentPassword, string newPassword)
    {
        if (VerifyPassword(currentPassword))
        {
            AccountPassword = newPassword;
        }
    }

    /// <summary>Fresh members see the degraded monitoring states the e2e
    /// backend produces (stale snapshot banner, zero KPIs, fleet errors); the
    /// demo account sees its seeded data.</summary>
    public bool MonitoringStale { get; set; } = true;

    /// <summary>Error the databases list renders for fresh members, matching
    /// the validation failure ts-dashboard's e2e backend returns in this
    /// state ("Invalid database page." + Retry).</summary>
    public string? DatabaseListError { get; set; }

    // --- Organization & projects -------------------------------------------

    public ImmutableArray<DashboardProject> Projects { get; set; } = [];

    public string ActiveProjectId { get; set; } = "";

    public ImmutableArray<DashboardDatabase> Databases { get; set; } = [];

    public ImmutableArray<ProjectApiKey> ApiKeys { get; set; } = [];

    public ImmutableArray<M2mApplication> M2mApplications { get; set; } = [];

    public ImmutableArray<TeamMember> TeamMembers { get; set; } = [];

    public ImmutableArray<Invitation> Invitations { get; set; } = [];

    public ImmutableArray<Notification> Notifications { get; set; } = [];

    public ImmutableArray<AuditEvent> AuditEvents { get; set; } = [];

    public UsageCycle Usage { get; set; } = new(
        Period: "Current cycle",
        StorageMb: 0,
        StorageLimitMb: 0,
        Requests: 0,
        RequestLimit: 0,
        ReadUnits: 0,
        WriteUnits: 0,
        InvoiceStatus: "",
        OverrunEstimateUsd: 0m);

    public ImmutableArray<(string Range, ImmutableArray<WorkloadPoint> Points)> Workloads { get; set; } = [];

    public ImmutableArray<SavedQuery> SavedQueries { get; set; } = [];

    public ImmutableArray<Session> Sessions { get; set; } = [];

    public ImmutableArray<WebhookEndpoint> Webhooks { get; set; } = [];

    public ImmutableArray<DashboardUser> AllUsers { get; set; } = [];

    // --- Feature collections -----------------------------------------------

    public ImmutableArray<M2mCredential> M2mCredentials { get; set; } = [];

    public (string CredentialId, string Name, string Secret)? PendingSecret { get; set; }

    public ImmutableArray<BackupRestorePoint> RestorePoints { get; set; } = [];

    public ImmutableArray<BackupRestoreJob> RestoreJobs { get; set; } = [];

    public ImmutableArray<RestorePlan> RestorePlans { get; set; } = [];

    public string BackupsLoadedAt { get; set; } = "";

    public ImmutableArray<BackupJob> BackupJobs { get; set; } = [];

    public ImmutableArray<ExplorerDocument> ExplorerDocuments { get; set; } = [];

    public SchemaDocument Schema { get; set; } = new([], Version: 1, UpdatedAt: "", Migration: null);

    public SchemaDiff? PendingSchemaDiff { get; set; }

    public string ProposedSchemaJson { get; set; } = "";

    public ImmutableArray<IndexState> Indexes { get; set; } = [];

    public ImmutableArray<QueryTemplate> QueryTemplates { get; set; } = [];

    public ImmutableArray<IndexRecommendation> IndexRecommendations { get; set; } = [];

    public string ProfileDisplayName { get; set; } = "";

    public string ProfilePhoneNumber { get; set; } = "";

    public ImmutableArray<string> MfaFactors { get; set; } = [];

    public ImmutableArray<string> PendingRecoveryCodes { get; set; } = [];

    public ImmutableArray<ProfilePasskeyView> Passkeys { get; set; } = [];

    public bool AuthenticationEnabled { get; set; } = true;

    public string SecuritySettingsUpdatedAt { get; set; } = "";

    public ImmutableArray<SettingsAuthProvider> AuthProviders { get; set; } = [];

    public ImmutableArray<DashboardStore.AdminUserRow> AdminUsers { get; set; } = [];

    public ImmutableArray<DashboardStore.AdminOrganizationRow> AdminOrganizations { get; set; } = [];

    public ImmutableArray<DashboardStore.AdminEstateActionRecord> AdminEstateActionsLog { get; set; } = [];

    public DashboardStore.AdminPlatformUsage AdminUsage { get; set; } = new(
        Organizations: 0,
        Projects: 0,
        Databases: 0,
        Usage:
        [
            new("organizations", 0, "count"),
            new("projects", 0, "count"),
            new("databases", 0, "count"),
        ]);

    /// <summary>Estate dashboard facts keyed by section; sections with no
    /// entry render the original "No data" empty state.</summary>
    public ImmutableDictionary<string, ImmutableArray<DashboardStore.AdminEstateFact>> AdminEstateFacts { get; set; } =
        ImmutableDictionary<string, ImmutableArray<DashboardStore.AdminEstateFact>>.Empty;

    public ImmutableArray<DashboardStore.InvoiceRow> Invoices { get; set; } = [];

    public bool PaidExcessEnabled { get; set; }

    public string? SpendCapUsd { get; set; }

    public Dictionary<string, string> DatabaseSpendCaps { get; set; } = [];

    public HashSet<string> BackupsDisabled { get; set; } = [];

    public Dictionary<string, (string Schedule, string Retention)> BackupPolicies { get; set; } = [];

    /// <summary>Wizard state; demo world starts mid-wizard, fresh accounts
    /// walk it from the organization step.</summary>
    public ImmutableArray<OnboardingWizardStep> OnboardingStepsValue { get; set; } =
    [
        new("account-information", "completed"),
        new("organization", "completed"),
        new("payment-method", "current"),
        new("payment-validation", "pending"),
        new("project", "pending"),
    ];

    // --- Helpers ------------------------------------------------------------

    public static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..24];

    public static string InitialsOf(string name, string email)
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

    // --- Seeds --------------------------------------------------------------

    private void SeedFresh(string displayName, string email)
    {
        OnboardingStepsValue =
        [
            new OnboardingWizardStep("account-information", "completed"),
            new OnboardingWizardStep("organization", "current"),
            new OnboardingWizardStep("payment-method", "pending"),
            new OnboardingWizardStep("payment-validation", "pending"),
            new OnboardingWizardStep("project", "pending"),
        ];

        OwnerSession = false;
        DatabaseListError = "Invalid database page.";

        CurrentUser = new DashboardUser(
            Id: NewId("usr"),
            Name: displayName,
            Email: email,
            Role: "owner",
            Status: "Active",
            MfaEnabled: false,
            AvatarInitials: InitialsOf(displayName, email),
            Provider: "password");
        RegisteredEmail = email;
        RegisteredName = displayName;

        var userId = CurrentUser.Id;
        ActiveOrganizationId = NewId("org");
        var projectId = NewId("prj");
        Projects = [new(projectId, ActiveOrganizationId, "Personal", "SFO", DateTimeOffset.UtcNow.ToString("O"), IsDefault: true)];
        ActiveProjectId = projectId;

        TeamMembers =
        [
            new(userId, displayName, email, "owner", "Active", false,
                DateTimeOffset.UtcNow.ToString("O"), 0),
        ];

        // The seeded organization invitation the invitations page renders.
        Invitations =
        [
            new("01J00000000000000000000000", "", "member", "Pending", "",
                DateTimeOffset.UtcNow.ToString("O"), "01J00000000000000000000000"),
        ];

        AllUsers = [.. TeamMembers.Select(m => new DashboardUser(
            m.UserId, m.Name, m.Email, m.Role, m.Status, m.MfaEnabled,
            InitialsOf(m.Name, m.Email), Provider: "password", OrganizationName: ActiveOrganizationName))];
    }

    private void SeedDemo()
    {
        MonitoringStale = false;
        DatabaseListError = null;
        OnboardingCompleted = true;

        CurrentUser = new DashboardUser(
            Id: "usr_01JC9Q7T4N8Y2M3P5R7V9X1ZAB",
            Name: "Mario Kraus",
            Email: "mario@terrascale.tech",
            Role: "owner",
            Status: "Active",
            MfaEnabled: true,
            AvatarInitials: "MK",
            Provider: "password");
        RegisteredEmail = "mario@terrascale.tech";
        RegisteredName = "Mario Kraus";
        ActiveOrganizationName = "TerraScale Labs";
        ActiveOrganizationId = "org_01JC9Q5V2N4P6T8R0X2Z4B6D8";

        Projects =
        [
            new("prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "org_01JC9Q5V2N4P6T8R0X2Z4B6D8", "core-platform", "us-east-1", "2026-01-12T09:24:00Z", IsDefault: true),
            new("prj_01JC9R2X4P6T8V1Z3B5D7F9HC", "org_01JC9Q5V2N4P6T8R0X2Z4B6D8", "edge-analytics", "eu-central-1", "2026-02-03T14:02:00Z"),
            new("prj_01JC9S4Y6R8T0V2X4Z6B8D1FE", "org_01JC9Q5V2N4P6T8R0X2Z4B6D8", "staging-sandbox", "sa-east-1", "2026-03-21T11:47:00Z"),
        ];
        ActiveProjectId = "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA";

        Databases =
        [
            new("db_01JC9Q8Y5N7P9R3T5V7X9Z2AC", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "core-primary", "Active", "us-east-1", "us-west-2", 4096, 128_450, "2026-01-12T09:31:00Z"),
            new("db_01JC9R3Z6P8T0V4X6Z8B2D4GE", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "core-sessions", "Active", "us-east-1", null, 512, 812_300, "2026-01-14T10:02:00Z"),
            new("db_01JC9S5A7R9V1X3Z5B7D9F3HI", "prj_01JC9R2X4P6T8V1Z3B5D7F9HC", "edge-events", "Active", "eu-central-1", "eu-west-1", 2048, 2_940_120, "2026-02-03T14:11:00Z"),
            new("db_01JC9T6B8S0X2Z4B6D8F1H3JK", "prj_01JC9S4Y6R8T0V2X4Z6B8D1FE", "sandbox", "Provisioning", "sa-east-1", null, 0, 0, "2026-09-17T08:15:00Z"),
        ];

        ApiKeys =
        [
            new("key_01JC9Q9Z6N8P1R4T7V0X3Z6B9", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "ci-deploy", "tsk_live_9f2", "4c1a", ["databases:read", "documents:write"], "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC", "mario@terrascale.tech", "2026-02-10T12:00:00Z", "2026-12-10T12:00:00Z", "2026-09-17T06:41:00Z", null),
            new("key_01JC9R4A8P0T3V6X9Z2C5E8HB", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "local-dev", "tsk_live_2b7", "8e3d", ["databases:read"], null, "mario@terrascale.tech", "2026-03-02T09:15:00Z", null, "2026-09-16T22:03:00Z", null),
            new("key_01JC9S6B9R1X4Z7B0D3F6J9LM", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "retired-key", "tsk_live_7d5", "0f92", ["*"], null, "mario@terrascale.tech", "2025-11-04T16:20:00Z", "2026-05-04T16:20:00Z", "2026-04-28T10:00:00Z", "2026-05-01T08:00:00Z"),
        ];

        M2mApplications =
        [
            new("m2m_01JC9Q8A2C4E6G8J0L2N4Q7ST", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "ingest-worker", "Active", "2026-02-18T08:00:00Z", "2026-09-18T04:12:00Z"),
            new("m2m_01JC9R9B3D5F7H9K1M3P5S8UV", "prj_01JC9R2X4P6T8V1Z3B5D7F9HC", "edge-sync", "Active", "2026-03-05T13:30:00Z", "2026-09-18T02:55:00Z"),
        ];

        TeamMembers =
        [
            new("usr_01JC9Q7T4N8Y2M3P5R7V9X1ZAB", "Mario Kraus", "mario@terrascale.tech", "owner", "Active", true, "2026-09-18T07:12:00Z", 2),
            new("usr_01JC9R8U5P0W3Y5A7C9E2G5IK", "Ana Souza", "ana@terrascale.tech", "admin", "Active", true, "2026-09-17T18:40:00Z", 1),
            new("usr_01JC9S9V6Q1X4Z6B8D0F3H6JN", "Ravi Patel", "ravi@terrascale.tech", "member", "Active", false, "2026-09-15T09:03:00Z", 0),
        ];

        Invitations =
        [
            new("inv_01JC9TAB2S4U6W8Y1B3D5F8HK", "jules@contractor.dev", "member", "Pending", "mario@terrascale.tech", "2026-09-16T15:00:00Z", "tok_01HXINVITEA2B4C"),
            new("inv_01JC9TBC3T5V7X9Z2C4E6G9JL", "former@terrascale.tech", "member", "Expired", "ana@terrascale.tech", "2026-07-02T11:30:00Z", "tok_01HXINVITEB3C5D"),
        ];

        Notifications =
        [
            new("ntf_01JC9UBD4U6W8Y0B3D5F7J2NP", "api-key-expiry", "API key expiring soon", "ci-deploy expires in 7 days. Rotate it to avoid breaking the pipeline.", "Unread", "warning", "2026-09-17T09:00:00Z"),
            new("ntf_01JC9UCE5V7X9Z1C4E6H1M4OQ", "invitation", "Invitation pending", "jules@contractor.dev has not accepted the invitation yet.", "Unread", "info", "2026-09-16T15:05:00Z"),
            new("ntf_01JC9UDF6W8Y0D2E5F7J2N5PR", "quota", "Storage at 82%", "core-primary is using 82% of the plan storage quota.", "Read", "warning", "2026-09-14T20:30:00Z"),
            new("ntf_01JC9UEG7X9Z1E3F6G8K3P6ST", "backup", "Backup completed", "core-primary nightly backup finished in 42s.", "Read", "success", "2026-09-14T03:12:00Z"),
        ];

        AuditEvents =
        [
            new("aud_01JC9VFH8Y0A2C5E8H0K4S8WX", "mario@terrascale.tech", "api_key.created", "ci-deploy", "api_key", "low", "dashboard", "2026-09-18T07:05:00Z"),
            new("aud_01JC9VGI9Z1B3D6F9I1L5T9YA", "ana@terrascale.tech", "member.role_changed", "ravi@terrascale.tech", "member", "medium", "dashboard", "2026-09-17T14:22:00Z"),
            new("aud_01JC9VHJ0B2C4E7G0J2M6U1ZB", "ci-deploy (api key)", "document.batch_upsert", "core-primary/orders", "database", "low", "api", "2026-09-17T06:41:00Z"),
            new("aud_01JC9VIK1C3D5F8H1K3N7V3AC", "mario@terrascale.tech", "database.deleted", "old-reporting", "database", "high", "dashboard", "2026-09-12T11:04:00Z"),
            new("aud_01JC9VJM2D4E6G9I2L4P8W5BD", "system", "backup.restore_completed", "core-sessions", "database", "medium", "scheduled", "2026-09-10T02:48:00Z"),
        ];

        Usage = new(
            Period: "2026-09 (cycle closes Oct 1)",
            StorageMb: 6656,
            StorageLimitMb: 8192,
            Requests: 4_812_640,
            RequestLimit: 5_000_000,
            ReadUnits: 31_204_500,
            WriteUnits: 8_140_200,
            InvoiceStatus: "Pending",
            OverrunEstimateUsd: 12.40m);

        Workloads =
        [
            ("24h", DashboardStore.SeedWorkload(24, 950, 180)),
            ("7d", DashboardStore.SeedWorkload(7 * 24, 880, 320)),
            ("30d", DashboardStore.SeedWorkload(30, 820, 420)),
        ];

        SavedQueries =
        [
            new("sq_01JC9XN4R6T8V1X4Z7B9D3F6H", "orders-over-500", "SELECT * FROM orders WHERE total > 500", "41", "2026-09-16T10:20:00Z", "ana@terrascale.tech"),
            new("sq_01JC9XP5S7U9W2Y5A8C1E5G7J", "sessions-active-7d", "SELECT id, startedAt FROM sessions WHERE startedAt > now() - 7d", "12", "2026-09-14T16:44:00Z", "mario@terrascale.tech"),
        ];

        Sessions =
        [
            new("ses_01JC9ZQ7T9V2X5A8C1E4G9K2N", "Chrome on Linux", "203.0.113.24", "São Paulo, BR", "2026-09-05T14:02:00Z", "2026-09-18T07:12:00Z", "password", true),
            new("ses_01JC9ZR8U1W3Y6B9D2F5H0M4P", "ts-cli", "203.0.113.24", "São Paulo, BR", "2026-09-16T18:30:00Z", "2026-09-17T22:41:00Z", "api-key", false),
            new("ses_01JC9ZS9V2X4Z7C0E3G6J3N6Q", "Safari on macOS", "198.51.100.7", "Frankfurt, DE", "2026-09-02T08:11:00Z", "2026-09-12T09:26:00Z", "github", false),
        ];

        Webhooks =
        [
            new("wh_01JCA0RBD2F4H7J0M3P6S1V4Y", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "https://ops.example.net/hooks/terrascale", "Active", "2026-02-11T09:00:00Z", "whsec_••••f4a9"),
        ];

        AllUsers =
        [
            new("usr_01JC9Q7T4N8Y2M3P5R7V9X1ZAB", "Mario Kraus", "mario@terrascale.tech", "owner", "Active", true, "MK", Provider: "password", SupportNote: "Org owner. Direct line.", OrganizationName: "TerraScale Labs"),
            new("usr_01JC9R8U5P0W3Y5A7C9E2G5IK", "Ana Souza", "ana@terrascale.tech", "admin", "Active", true, "AS", Provider: "github", OrganizationName: "TerraScale Labs"),
            new("usr_01JC9S9V6Q1X4Z6B8D0F3H6JN", "Ravi Patel", "ravi@terrascale.tech", "member", "Active", false, "RP", Provider: "google", OrganizationName: "TerraScale Labs"),
            new("usr_01JC9TA1Z3C5E7G9I1K4M7P0S", "Dana White", "dana@northwind.io", "owner", "Suspended", false, "DW", Provider: "sso", SupportNote: "Billing dispute open.", OrganizationName: "Northwind"),
            new("usr_01JC9TB2D4F6H8J0L2N5P8S1V", "Igor Ivanov", "igor@northwind.io", "member", "Active", true, "II", Provider: "password", OrganizationName: "Northwind"),
        ];

        M2mCredentials =
        [
            new("m2m_01JC9Q8A2C4E6G8J0L2N4Q7ST", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "ingest-worker",
                "tsm_live_4e1", "7ab2", ["documents:write"], "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC",
                "mario@terrascale.tech", "2026-02-18T08:00:00Z", null, "2026-09-18T04:12:00Z", null),
            new("m2m_01JC9R9B3D5F7H9K1M3P5S8UV", "prj_01JC9R2X4P6T8V1Z3B5D7F9HC", "edge-sync",
                "tsm_live_b90", "3f17", ["documents:read", "documents:write"], "",
                "ana@terrascale.tech", "2026-03-05T13:30:00Z", "2026-09-30T00:00:00Z", "2026-09-18T02:55:00Z", null),
        ];

        RestorePoints =
        [
            new("gen_01JCB1A3C5E7G9J1K3M5P7R9T", "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC", "core-primary", "us-east-1",
                "Full", true, 1284, 41_940_812L, "2026-09-18T02:04:00Z"),
            new("gen_01JCB2B4D6F8H0K2L4N6Q8SAV", "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC", "core-primary", "us-east-1",
                "Incremental", true, 96, 3_145_728L, "2026-09-18T08:02:00Z"),
            new("gen_01JCB3C5E7G9I1K3M5O7S9UBW", "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC", "core-primary", "us-west-2",
                "Snapshot", true, 1284, 41_940_812L, "2026-09-17T02:03:00Z"),
            new("gen_01JCB4D6F8H0J2L4N6P8TAVCX", "db_01JC9R3Z6P8T0V4X6Z8B2D4GE", "core-sessions", "us-east-1",
                "Full", false, 512, 18_874_368L, "2026-09-18T02:07:00Z"),
            new("gen_01JCB5E7G9I1K3M5O7Q9UAWDY", "db_01JC9S5A7R9V1X3Z5B7D9F3HI", "edge-events", "eu-central-1",
                "Incremental", true, 240, 7_340_032L, "2026-09-18T06:02:00Z"),
        ];

        RestoreJobs =
        [
            new("res_01JCB6F8H0J2L4N6P8R0VAXEZ", "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC", "core-primary", "us-east-1",
                "core-primary-restored-0904", "Succeeded", "2026-09-10T02:41:00Z", "2026-09-10T02:48:00Z", null),
            new("res_01JCB7G9I1K3M5O7Q9S1WBYFA", "db_01JC9R3Z6P8T0V4X6Z8B2D4GE", "core-sessions", "us-east-1",
                "core-sessions-restored-0831", "Failed", "2026-09-06T08:22:00Z", "2026-09-06T08:25:00Z",
                "Incremental chain incomplete: missing base generation for cutoff 2026-09-05T02:07:00Z."),
        ];

        BackupsLoadedAt = "2026-09-18T09:12:00Z";

        BackupJobs =
        [
            new("bkj_01JCC1A3C5E7G9J1K3M5P7R9T", "core-primary", "Daily 03:00 UTC", "Succeeded", "2026-09-19T03:04:00Z"),
            new("bkj_01JCC2B4D6F8H0K2L4N6Q8SAV", "core-sessions", "Daily 03:00 UTC", "Succeeded", "2026-09-19T03:07:00Z"),
            new("bkj_01JCC3C5E7G9I1K3M5O7S9UBW", "core-primary", "On-demand", "Succeeded", "2026-09-18T21:12:00Z"),
        ];

        ExplorerDocuments =
        [
            new("doc_01JC9XQ1T3V5X7Z9B2D4F7HJ2", """{ "_id": "doc_01JC9XQ1T3V5X7Z9B2D4F7HJ2", "status": "active", "customer": "northwind", "total": 640 }"""),
            new("doc_01JC9XR2U4W6Y8A1C3E6G9K4M", """{ "_id": "doc_01JC9XR2U4W6Y8A1C3E6G9K4M", "status": "active", "customer": "acme", "total": 128 }"""),
            new("doc_01JC9XS3V5X7Z9B2D4F8H1N5P", """{ "_id": "doc_01JC9XS3V5X7Z9B2D4F8H1N5P", "status": "refunded", "customer": "northwind", "total": 42 }"""),
            new("doc_01JC9XT4W6Y8A1C3E7G2L6Q7R", """{ "_id": "doc_01JC9XT4W6Y8A1C3E7G2L6Q7R", "status": "active", "customer": "globex", "total": 902 }"""),
        ];

        Schema = new(
            Collections:
            [
                new("orders",
                [
                    new("id", "string", Required: true, Indexed: true),
                    new("status", "string", Required: true, Indexed: true),
                    new("customer", "string", Required: false, Indexed: false),
                    new("total", "number", Required: true, Indexed: false),
                    new("createdAt", "string", Required: true, Indexed: false),
                ],
                [new("orders_status_idx", ["status"]), new("orders_id_idx", ["id"])],
                HistoryRetentionDays: 30),
                new("sessions",
                [
                    new("id", "string", Required: true, Indexed: true),
                    new("startedAt", "string", Required: true, Indexed: false),
                    new("principal", "string", Required: false, Indexed: false),
                ],
                [new("sessions_id_idx", ["id"])]),
            ],
            Version: 4,
            UpdatedAt: "2026-09-10T14:22:00Z",
            Migration: new("complete", 12_480, 12_480, "wal-01JC9M2Q7V"));

        Indexes =
        [
            new("idx_01JC9YA3C5E7G9J1L4N7Q2S5V", "orders_id_idx", ["id"], "active", 42, 0, null, "2026-09-17T22:41:00Z"),
            new("idx_01JC9YB4D6F8H1K3M5P8R3T6W", "orders_status_idx", ["status"], "active", 88, 0, null, "2026-09-17T22:41:00Z"),
            new("idx_01JC9YC5E7G9J2L4N6Q1S8U9X", "orders_customer_total_idx", ["customer", "total"], "building", null, 3_140, "doc_01JC9XS3V5X7Z9B2D4F8H1N5P", null),
        ];

        QueryTemplates =
        [
            new("tpl_01JC9YR6F8H1K3M5P7R2T5W8Z", "SELECT * FROM orders WHERE status = $1 LIMIT 25", 14_820, 3, 44_460, 6),
            new("tpl_01JC9YS7G9J2L4N6Q8S3U6X1A", "SELECT id, total FROM orders WHERE customer = $1 ORDER BY total DESC", 3_206, 9, 28_854, 14),
            new("tpl_01JC9YT8H1K3M5P7R9T4U7Y2B", "SELECT * FROM sessions WHERE startedAt > $1", 918, 12, 11_016, 21),
        ];

        IndexRecommendations =
        [
            new("rec_01JC9ZZ4N6Q1S4V7X9B2D5G8J", ["customer", "total"], "Template tpl_01JC9YS7G9J2L4N6Q8S3U6X1A scans 3,206 executions without a covering index.", 71, "recommended"),
            new("rec_01JCA0A5P7R2T5W8Y1C3E6H9K", ["startedAt"], "Template tpl_01JC9YT8H1K3M5P7R9T4U7Y2B filters on an unindexed column.", 44, "applied"),
        ];

        ProfilePhoneNumber = "+55 11 90000-0000";
        MfaFactors = ["totp"];
        Passkeys = [new("pass_01JC9ZQ7T9V2X5A8C1E4G9K2N", "YubiKey 5C", "2026-05-02T10:11:00Z")];

        SecuritySettingsUpdatedAt = "2026-08-30T14:22:00Z";
        AuthProviders =
        [
            new("entra-id", "Microsoft Entra ID", "OIDC", "Enabled", false,
                "00000000-0000-0000-0000-000000000000", "Secret configured",
                "https://login.microsoftonline.com/9c1f4d2e-4f6b-4a7e-9d3a-1b2c3d4e5f6a/oauth2/v2.0/authorize",
                "https://login.microsoftonline.com/9c1f4d2e-4f6b-4a7e-9d3a-1b2c3d4e5f6a/oauth2/v2.0/token",
                "https://graph.microsoft.com/oidc/userinfo",
                "https://dashboard.terrascale.tech/api/v1/auth/callback",
                "", "", "", "", "2026-08-30T14:22:00Z"),
            new("okta-saml", "Okta (SAML)", "SAML", "Enabled", false,
                "", "",
                "", "", "", "",
                "https://terrascale.okta.com/saml/sp/01JC9Q7T4N8Y2M3P", "https://terrascale.okta.com/app/terrascale/sso/saml",
                "Certificate valid until 2027-04-12", "https://dashboard.terrascale.tech/api/v1/auth/saml/acs",
                "2026-07-18T09:40:00Z"),
        ];


        AdminUsers =
        [
            new("usr_01JC9Q7T4N8Y2M3P5R7V9X1ZAB", "Mario Kraus", "mario@terrascale.tech", true, true, "2026-01-12T09:24:00Z"),
            new("usr_01JC9R8U5P0W3Y5A7C9E2G5IK", "Ana Souza", "ana@terrascale.tech", true, false, "2026-02-04T11:02:00Z"),
            new("usr_01JC9S9V6Q1X4Z6B8D0F3H6JN", "Ravi Patel", "ravi@terrascale.tech", true, false, "2026-02-27T15:41:00Z"),
            new("usr_01JC9TA1Z3C5E7G9I1K4M7P0S", "Dana White", "dana@northwind.io", false, true, "2025-11-18T08:10:00Z"),
            new("usr_01JC9TB2D4F6H8J0L2N5P8S1V", "Igor Ivanov", "igor@northwind.io", true, false, "2026-03-08T17:26:00Z"),
        ];

        AdminOrganizations =
        [
            new("org_01JC9Q5V2N4P6T8R0X2Z4B6D8", "TerraScale Labs", "Active", "2026-01-10T14:00:00Z", 3, 1, "Healthy"),
            new("org_01JCA2K4M6P8R0T3W5Y8A1C4E", "Northwind", "Active", "2025-11-15T09:30:00Z", 2, 1, "Dunning"),
            new("org_01JCA3L5N7Q9S1U4X6Z9B2D5F", "Helios Research", "Suspended", "2025-09-02T12:00:00Z", 1, 0, "Suspended"),
        ];

        AdminUsage = new(
            Organizations: 3,
            Projects: 6,
            Databases: 4,
            Usage:
            [
                new("organizations", 3, "count"),
                new("projects", 6, "count"),
                new("databases", 4, "count"),
            ]);

        AdminEstateFacts = new Dictionary<string, DashboardStore.AdminEstateFact[]>
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

        Invoices =
        [
            new("in_01JCA2QG4M6P8S1V3X5Z7B9DF", 2900, "USD", "paid", "2026-08-01T00:00:00Z", "2026-09-01T00:00:00Z", "2026-09-01T08:02:00Z",
            "https://pay.terrascale.tech/i/acct_1TERRAscale/in_01JCA2QG4M6P8S1V3X5Z7B9DF",
            "https://pay.terrascale.tech/i/acct_1TERRAscale/in_01JCA2QG4M6P8S1V3X5Z7B9DF/pdf"),
            new("in_01JCA3RH5N7Q9T2W4Y6A8C2EG", 4140, "USD", "open", "2026-09-01T00:00:00Z", "2026-10-01T00:00:00Z", "2026-10-01T08:00:00Z",
            "https://pay.terrascale.tech/i/acct_1TERRAscale/in_01JCA3RH5N7Q9T2W4Y6A8C2EG",
            "https://pay.terrascale.tech/i/acct_1TERRAscale/in_01JCA3RH5N7Q9T2W4Y6A8C2EG/pdf"),
        ];

        AccountLocale = "en";
        AccountPassword = Features.Auth.PasswordLoginEndpoint.DemoPassword;
    }
}
