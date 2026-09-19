using System.Collections.Immutable;

namespace TerraScale.Dashboard2;

public sealed record NavRoute(
    RouteId Id,
    string Href,
    string Label,
    string Title,
    string Eyebrow,
    string Summary,
    string Icon,          // Icons method name
    string PrimaryAction,
    string? SecondaryAction = null,
    string? CreateAction = null,
    bool AdminOnly = false)
{
    public string Accent => NavAccents[Id];
    public bool HasCreate => CreateAction is not null;

    public static readonly ImmutableArray<NavRoute> Main =
    [
        new(RouteId.Home, "/", "Overview", "Operations Overview", "Current project",
            "Project, database, usage, team, and notification state for the active TerraScale project.",
            "Home", "Open connection center", "Provision database"),
        new(RouteId.Databases, "/databases", "Databases", "Databases", "Logical database fleet",
            "Provisioning, region placement, replication, backups, restores, and capacity.",
            "Database", "Create database", "Restore backup", "database"),
        new(RouteId.Data, "/data", "Tables", "Data Explorer", "Saved queries",
            "Project queries with revision-protected editing.",
            "Table", "Save query", null, "query"),
        new(RouteId.ApiKeys, "/api-keys", "API keys", "API Keys and M2M Clients", "Programmatic access",
            "Scoped API keys, machine clients, rotation, and revocation.",
            "KeyRound", "Create key", "Create M2M client", "api-key"),
        new(RouteId.Access, "/access", "Team", "Team and Access", "Project membership",
            "Members, invitations, roles, permissions, sessions, MFA posture, SSO, and SCIM status.",
            "Users", "Invite member", "Review roles", "invitation"),
        new(RouteId.Usage, "/usage", "Usage", "Usage and Billing", "Plan and limits",
            "Current-cycle usage, plan limits, pricing summary, invoice status, and projected overage signals.",
            "Gauge", "Review plan", "Export usage"),
        new(RouteId.Engine, "/engine", "Engine", "Engine", "TerraBase observability",
            "Live TerraBase member health, region catalog, and the Prometheus metric surface.",
            "Activity", "Refresh engine"),
        new(RouteId.Audit, "/audit", "Logs", "Audit", "Security evidence",
            "Customer-visible security audit entries without operation feeds or generic activity timelines.",
            "ScrollText", "Export audit", "Filter events"),
        new(RouteId.Notifications, "/notifications", "Notifications", "Notifications", "Actionable inbox",
            "Bounded notification center for API key expiry, pending invitations, backup issues, and quota thresholds.",
            "Bell", "Dismiss read", "Notification settings"),
        new(RouteId.Settings, "/settings", "Settings", "Settings", "Project controls",
            "Security controls, webhook endpoints, SSO, SCIM, backups, and project configuration.",
            "Settings", "Save settings", "Create webhook"),
        new(RouteId.Profile, "/profile", "Profile", "Profile", "Account",
            "Account profile, preferences, sessions, trusted devices, and notification delivery choices.",
            "UserRound", "Save profile", "Manage sessions"),
        new(RouteId.Organization, "/organization", "Organization", "Organization", "Tenant context",
            "Organization details, project switching, invitations, ownership, and tenant bookkeeping.",
            "Building", "Create project", "Switch project", "project"),
    ];

    public static readonly ImmutableArray<NavRoute> Admin =
    [
        new(RouteId.AdminUsers, "/admin/users", "Users", "Admin Users", "Global users",
            "User lookup, creation, role changes, status changes, deletion, and support notes.",
            "Fingerprint", "Create user", "Add support note", AdminOnly: true),
        new(RouteId.AdminDatabases, "/admin/databases", "Databases", "Admin Databases", "Global database state",
            "Cross-tenant logical database, region placement, capacity, backup, and restore oversight.",
            "Network", "Inspect placement", AdminOnly: true),
        new(RouteId.AdminEstate, "/admin/estate", "Estate", "Admin Estate", "Global placement",
            "Live region state and per-database placement across the estate.",
            "Network", "Inspect placement", AdminOnly: true),
        new(RouteId.AdminOrganizations, "/admin/organizations", "Organizations", "Admin Organizations", "Global tenants",
            "Organization lookup, project counts, owner state, billing posture, and support context.",
            "Building2", "Lookup organization", AdminOnly: true),
        new(RouteId.AdminUsage, "/admin/usage", "Usage", "Admin Usage", "Global usage",
            "Cross-tenant usage, quota pressure, revenue signals, and pricing operations.",
            "Activity", "Review quotas", AdminOnly: true),
        new(RouteId.AdminPricing, "/admin/pricing", "Pricing", "Admin Pricing", "Commercial controls",
            "Plan catalog, pricing summaries, invoice state, credits, and owner-only commercial actions.",
            "LockKeyhole", "Review pricing", AdminOnly: true),
    ];

    public static readonly ImmutableArray<NavRoute> All = [.. Main, .. Admin];

    public static NavRoute? Find(string href) => All.FirstOrDefault(r => r.Href == href);

    private static readonly ImmutableDictionary<RouteId, string> NavAccents =
        new Dictionary<RouteId, string>
        {
            [RouteId.Home] = "#3B82F6",
            [RouteId.Databases] = "#8B5CF6",
            [RouteId.Data] = "#14B8A6",
            [RouteId.ApiKeys] = "#F59E0B",
            [RouteId.Access] = "#EC4899",
            [RouteId.Usage] = "#22C55E",
            [RouteId.Engine] = "#06B6D4",
            [RouteId.Audit] = "#F97316",
            [RouteId.Notifications] = "#EAB308",
            [RouteId.Settings] = "#64748B",
            [RouteId.Profile] = "#A855F7",
            [RouteId.Organization] = "#EF4444",
            [RouteId.AdminEstate] = "#6366F1",
            [RouteId.AdminUsers] = "#D946EF",
            [RouteId.AdminDatabases] = "#0EA5E9",
            [RouteId.AdminOrganizations] = "#84CC16",
            [RouteId.AdminUsage] = "#10B981",
            [RouteId.AdminPricing] = "#F43F5E",
        }.ToImmutableDictionary();
}
