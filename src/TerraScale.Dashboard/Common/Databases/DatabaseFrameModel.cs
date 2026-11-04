namespace TerraScale.Dashboard.Common.Databases
{
    // Serving: the regions the database serves, when the page knows more than its record (an all-regions database).
    public sealed record DatabaseFrameModel(DatabaseRecord Database, string Tab, string Collection = "", IReadOnlyList<string>? Serving = null)
    {
        public static readonly (string Key, string Label)[] Tabs = [("overview", "Overview"), ("tables", "Tables"), ("sql", "SQL editor"), ("functions", "Functions"), ("replication", "Replication"), ("logs", "Logs"), ("backups", "Backups"), ("branches", "Branches"), ("settings", "Settings"), ("danger", "Danger zone")];
        public static string Title(string section)
        {
            return section switch
            {
                "documents" => "Documents",
                "schema" => "Schema",
                "indexes" => "Indexes",
                "import" => "Import and export",
                "history" => "Document history",
                _ => Tabs.FirstOrDefault(tab => tab.Key == section).Label ?? section
            };
        }
    }
}
