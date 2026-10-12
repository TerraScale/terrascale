using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Databases;

public sealed record DatabasesPage(ShellModel Shell, IReadOnlyList<DatabaseRecord> Databases, IReadOnlyList<DatabaseRegion> Choices,
    IReadOnlyList<CollectionStats> Contents, string Search = "", string Error = "", bool OpenCreate = false, string Name = "", IReadOnlyList<string>? Selection = null, string RegionsError = "")
{
    public AccountContext Context => Shell.Context;
    public IReadOnlyList<DatabaseRecord> All => Context.Project?.Databases.Values.OrderBy(database => database.Name, StringComparer.Ordinal).ToArray() ?? [];
    public int Ready => All.Count(database => database.Status == "ready");
    public int Progress => All.Count(database => database.Status is "provisioning" or "accepted" or "pending");
    public int Failed => All.Count(database => database.Status == "failed");
    public IReadOnlyList<string> Regions => All.SelectMany(DatabaseService.Regions).Distinct().Order().ToArray();
    public IReadOnlyList<string> Selected => Selection ?? (Context.Project is null || Context.Project.Region.Length == 0 ? [] : [Context.Project.Region]);
    public bool CanProvision => Context.Organization?.CanProvision(Context.UserId) ?? false;
    public string Description => Search.Length == 0 ? $"{Databases.Count} {(Databases.Count == 1 ? "database" : "databases")} in this project" : $"{Databases.Count} {(Databases.Count == 1 ? "database" : "databases")} matching \"{Search}\"";
    public static string Tone(string status) => status switch { "ready" => "emerald", "failed" => "rose", "provisioning" or "accepted" or "pending" => "amber", _ => "slate" };
    public static string RegionDisplay(string code) => code.ToUpperInvariant() switch { "SFO" => "SFO · San Francisco", "GRU" => "GRU · São Paulo", "" => "Not configured", _ => code.ToUpperInvariant() };
    public static string Bytes(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KiB" : $"{bytes / (1024d * 1024):0.0} MiB";
}

public sealed record DatabaseDetailPage(ShellModel Shell, DatabaseRecord Database, string Tab, IReadOnlyList<DatabaseRegion> Choices,
    IReadOnlyList<CollectionStats> Contents, string Error = "", string RegionsError = "", bool OpenDelete = false, bool Created = false, TerraScale.Dashboard.Features.Backups.BackupPage? Backups = null,
    bool ContentsAvailable = false, TerraScale.Dashboard.Features.Usage.UsageReplication? Measurements = null, string Range = "24h")
{
    public AccountContext Context => Shell.Context;
    public IReadOnlyList<DatabaseRecord> Branches => Context.Project!.Databases.Values.Where(database => database.BranchedFrom == Database.Id).OrderBy(database => database.Name, StringComparer.Ordinal).ToArray();
    public string Placement => DatabaseService.Regions(Database).Count == 0 ? DatabasesPage.RegionDisplay("")
        : string.Join(", ", DatabaseService.Regions(Database).Select(DatabasesPage.RegionDisplay));
    public (string Tone, string Title, string Body) CreatedState => DatabasesPage.Tone(Database.Status) switch
    {
        "emerald" => ("success", Database.Name + " is ready", DatabaseService.Regions(Database).Count == 1
            ? "It serves " + Placement + ". Add a collection in Documents, or connect with an API key."
            : "Its home region is ready. Replica progress is shown below. Add a collection in Documents, or connect with an API key."),
        "rose" => ("danger", Database.Name + " could not be created", "The data plane did not finish creating it. Delete it and create it again, or contact support if it keeps failing."),
        _ => ("info", Database.Name + " is being created", "The data plane is still placing it in " + Placement + ". Its status changes to ready when it can take requests.")
    };
    public IReadOnlyList<string> PlacementLabels
    {
        get
        {
            var configured = DatabaseService.Regions(Database);
            var labels = configured.Select(DatabasesPage.RegionDisplay).ToList();
            if (Context.Project?.BackupRegion is { Length: > 0 } backup && !configured.Contains(backup, StringComparer.OrdinalIgnoreCase))
                labels.Add(DatabasesPage.RegionDisplay(backup) + " · backup region");
            return labels;
        }
    }
    public string Replication => DatabaseService.Regions(Database) switch
    {
        { Count: 0 } => "Not reported",
        { Count: 1 } regions => "Single region · " + regions[0].ToUpperInvariant() + " only",
        var regions => "Replicated across " + string.Join(", ", regions).ToUpperInvariant()
    };
}

public sealed record DatabaseReplicationModel(TerraScale.Dashboard.Features.Usage.UsageReplication Reading, string ProjectId, string DatabaseId, string Range)
{
    public string AnalyticsPath => "/analytics?database=" + Uri.EscapeDataString(DatabaseId) + "&project=" + Uri.EscapeDataString(ProjectId) + "&range=" + Range;
}

public sealed record DatabaseFrameModel(DatabaseRecord Database, string Tab, string Collection = "")
{
    public static readonly (string Key, string Label)[] Tabs = [("overview", "Overview"), ("documents", "Documents"), ("sql", "SQL"), ("schema", "Schema"), ("indexes", "Indexes"), ("backups", "Backups"), ("branches", "Branches"), ("import", "Import and export")];
    public static string Title(string section) => section switch
    {
        "sql" => "SQL console",
        "history" => "Document history",
        _ => Tabs.FirstOrDefault(tab => tab.Key == section).Label ?? section
    };
}
