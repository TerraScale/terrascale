namespace TerraScale.Dashboard.Platform.Persistence;

// TerraBase seeds this database under the ts-dashboard project at startup.
// ts-auth owns the matching TerraScale organization, owner and project identities.
public static class DashboardStateSchema
{
    public const string ProjectId = "00000000000000000000000005";
    public const string Database = "ts-dashboard";

    public static IReadOnlyList<string> Collections { get; } = Array.AsReadOnly(new[]
    {
        "sessions",
        "account_deletions",
        "dashboard_deletion_cleanup",
        "dashboard_account_enrollments",
        "auth_credentials",
        "session_indexes",
        "auth-flows",
        "dashboard_accounts",
        "dashboard_account_identities",
        "dashboard_organizations",
        "dashboard_access_tokens",
        "dashboard_access_token_hashes",
        "dashboard_mcp_oauth_clients",
        "dashboard_mcp_oauth_codes",
        "dashboard_saved_queries",
        "dashboard_sql_workspaces",
        "dashboard_sql_database_deletions",
        "dashboard_billing",
        "dashboard_internal_billing",
        "dashboard_stripe_customers",
        "dashboard_billing_changes",
        "dashboard_stripe_checkouts",
        "dashboard_allowance_alerts",
        "dashboard_alert_rules",
        "dashboard_alert_projects",
        "dashboard_log_drains",
        "dashboard_log_drain_projects"
    });
}
