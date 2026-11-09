namespace TerraScale.Dashboard.Platform.Persistence
{
    public static class DashboardStateSchema
    {
        public const string ProjectId = "00000000000000000000000005";
        public const string Database = "ts-dashboard";

        public static IReadOnlyList<string> Tables { get; } = Array.AsReadOnly(
        [
            "account_deletions",
            "dashboard_deletion_cleanup"
        ]);

        public static IReadOnlyList<string> Statements { get; } = Array.AsReadOnly(
        [
            .. Tables.Select(table => $"CREATE TABLE IF NOT EXISTS {table} (id TEXT PRIMARY KEY NOT NULL, version TEXT NOT NULL, body TEXT NOT NULL)")
        ]);
    }
}
