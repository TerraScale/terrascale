namespace TerraScale.Dashboard.Common.Databases;

public sealed record DatabaseFrameModel(DatabaseRecord Database, string Tab, IReadOnlyList<string>? Serving = null, IReadOnlyCollection<DatabaseRecord>? Databases = null)
{
    public static readonly (string Key, string Label)[] Tabs = [("overview", "Overview"), ("connect", "Connect"), ("tables", "Tables"), ("sql", "SQL editor"), ("rules", "Rules"), ("functions", "Functions"), ("replication", "Replication"), ("logs", "Logs"), ("backups", "Backups"), ("settings", "Settings"), ("conflicts", "Conflicts"), ("branches", "Forks"), ("danger", "Danger zone")];

    public static string Title(string section)
    {
        return Tabs.FirstOrDefault(tab => tab.Key == section).Label ?? section;
    }

    public static bool IsMore(string section)
    {
        return section is "conflicts" or "branches" or "danger";
    }

    public IEnumerable<(string Key, string Label)> Primary => Tabs.Where(tab => !IsMore(tab.Key));

    public IEnumerable<(string Key, string Label)> More => Tabs.Where(tab => IsMore(tab.Key));

    public IReadOnlyList<string> Regions => Serving ?? DatabaseService.ServingRegions(Database);

    public IReadOnlyList<DatabaseRecord> Switchable => [.. (Databases is { Count: > 0 } listed ? listed : [Database]).OrderBy(database => database.Name, StringComparer.Ordinal)];
}
