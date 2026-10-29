namespace TerraScale.Dashboard.Common.Databases
{
    // Serving: the regions the database serves, when the page knows more than its record (an all-regions database).
    public sealed record DatabaseFrameModel(DatabaseRecord Database, string Tab, string Collection = "", IReadOnlyList<string>? Serving = null)
    {
        public static readonly (string Key, string Label)[] Tabs = [("overview", "Overview"), ("documents", "Documents"), ("sql", "SQL"), ("schema", "Schema"), ("indexes", "Indexes"), ("webhooks", "Webhooks"), ("functions", "Functions"), ("replication", "Replication"), ("logs", "Logs"), ("backups", "Backups"), ("branches", "Branches"), ("import", "Import and export"), ("settings", "Settings"), ("danger", "Danger zone")];
        public static string Title(string section)
        {
            return section switch
            {
                "sql" => "SQL console",
                "history" => "Document history",
                _ => Tabs.FirstOrDefault(tab => tab.Key == section).Label ?? section
            };
        }
    }
}
