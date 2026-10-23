namespace TerraScale.Dashboard.Shared.Databases;

public sealed record DatabaseFrameModel(DatabaseRecord Database, string Tab, string Collection = "")
{
    public static readonly (string Key, string Label)[] Tabs = [("overview", "Overview"), ("documents", "Documents"), ("sql", "SQL"), ("schema", "Schema"), ("indexes", "Indexes"), ("webhooks", "Webhooks"), ("replication", "Replication"), ("logs", "Logs"), ("backups", "Backups"), ("branches", "Branches"), ("import", "Import and export"), ("danger", "Danger zone")];
    public static string Title(string section) => section switch
    {
        "sql" => "SQL console",
        "history" => "Document history",
        _ => Tabs.FirstOrDefault(tab => tab.Key == section).Label ?? section
    };
}
