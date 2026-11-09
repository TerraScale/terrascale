namespace TerraScale.Dashboard.Platform.Persistence
{
    public static class DashboardStateSchema
    {
        public const string ProjectId = "00000000000000000000000005";
        public const string Database = "ts-dashboard";

        public static IReadOnlyList<string> Tables { get; } = Array.AsReadOnly(
        [
            "account_deletions",
            "dashboard_deletion_cleanup",
            "dashboard_accounts",
            "dashboard_account_identities",
            "dashboard_organizations",
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
        ]);

        public static IReadOnlyList<string> Statements { get; } = Array.AsReadOnly(
        [
            .. Tables.Select(table => $"CREATE TABLE IF NOT EXISTS {table} (id TEXT PRIMARY KEY NOT NULL, version TEXT NOT NULL, body TEXT NOT NULL)"),
            "CREATE INDEX IF NOT EXISTS dashboard_sql_workspaces_owner ON dashboard_sql_workspaces (json_extract(body, '$.OrganizationId'), json_extract(body, '$.ProjectId'), json_extract(body, '$.DatabaseId'))",
            "CREATE INDEX IF NOT EXISTS dashboard_sql_database_deletions_owner ON dashboard_sql_database_deletions (json_extract(body, '$.OrganizationId'), json_extract(body, '$.ProjectId'))"
        ]);
    }
}
