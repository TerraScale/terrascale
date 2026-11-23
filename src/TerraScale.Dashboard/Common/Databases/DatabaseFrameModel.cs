namespace TerraScale.Dashboard.Common.Databases
{
    public sealed record DatabaseFrameModel(DatabaseRecord Database, string Tab, IReadOnlyList<string>? Serving = null)
    {
        public static readonly (string Key, string Label)[] Tabs = [("overview", "Overview"), ("tables", "Tables"), ("rules", "Rules"), ("sql", "SQL editor"), ("functions", "Functions"), ("replication", "Replication"), ("conflicts", "Conflicts"), ("logs", "Logs"), ("backups", "Backups"), ("branches", "Forks"), ("settings", "Settings"), ("danger", "Danger zone")];
        public static string Title(string section)
        {
            return Tabs.FirstOrDefault(tab => tab.Key == section).Label ?? section;
        }
    }
}
