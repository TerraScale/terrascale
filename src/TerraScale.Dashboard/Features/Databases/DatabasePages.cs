using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Databases;

public sealed record DatabasesPage(ShellModel Shell, IReadOnlyList<DatabaseRecord> Databases, IReadOnlyList<DatabaseRegion> Choices,
    IReadOnlyList<CollectionStats> Contents, string Search = "", string Error = "", bool OpenCreate = false, string Name = "", IReadOnlyList<string>? Selection = null, string RegionsError = "")
{
    public bool ContentsAvailable { get; init; }
    public AccountContext Context => Shell.Context;
    public IReadOnlyList<DatabaseRecord> All => Context.Project?.Databases.Values.OrderBy(database => database.Name, StringComparer.Ordinal).ToArray() ?? [];
    public int Ready => All.Count(database => database.Status == "ready");
    public int Progress => All.Count(database => database.Status is "provisioning" or "accepted" or "pending");
    public int Failed => All.Count(database => database.Status == "failed");
    public IReadOnlyList<string> Regions => All.SelectMany(DatabaseService.Regions).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(TerraScale.Dashboard.Shared.UI.Regions.Rank).ThenBy(code => code, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<string> Selected => Selection ?? (Context.Project is null || Context.Project.Region.Length == 0 ? [] : [Context.Project.Region]);
    public bool CanProvision => Context.Organization?.CanProvision(Context.UserId) ?? false;
    public string Description => Search.Length == 0 ? $"{Databases.Count} {(Databases.Count == 1 ? "database" : "databases")} in this project" : $"{Databases.Count} {(Databases.Count == 1 ? "database" : "databases")} matching \"{Search}\"";
    public static string Tone(string status) => status switch { "ready" => "success", "failed" => "danger", "provisioning" or "accepted" or "pending" => "warning", _ => "neutral" };
    public const string CreatePlacementHint = "Choose one home region to create copies in every currently serving region. Choose two or more regions for explicit placement in only those regions; the first selected region is home. Without All regions, new regions are not added automatically.";
    public static string RegionDisplay(string code) => code switch { "" => "Not configured", _ => TerraScale.Dashboard.Shared.UI.Regions.Name(code) is { Length: > 0 } name ? $"{code.ToLowerInvariant()} · {name}" : code.ToLowerInvariant() };
    public static string Bytes(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KiB" : $"{bytes / (1024d * 1024):0.0} MiB";
    public DatabaseListMetrics? Metrics { get; init; }
    public string Range { get; init; } = "24h";
    // Range links keep the search filter.
    public string RangePath(string range) => "/databases?" + (Search.Length > 0 ? "q=" + Uri.EscapeDataString(Search) + "&" : "") + "range=" + range + "#project-databases";
    // Retrying the counts reloads the same filter and range.
    public string RetryPath => "/databases?" + (Search.Length > 0 ? "q=" + Uri.EscapeDataString(Search) + "&" : "") + "range=" + Range;
    // Clearing the search keeps the selected range.
    public string ClearPath => "/databases?range=" + Range + "#project-databases";
    public IEnumerable<(string Label, string? Value, string Title, AnalyticsLinePlot? Trend, string Hue)> Cells(string databaseId)
    {
        var metric = Metrics?.Databases.GetValueOrDefault(databaseId);
        string N(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        yield return ("Reads", metric?.Reads is { } reads ? TerraScale.Dashboard.Shared.Charts.ChartGeometry.Compact(reads) : null, metric?.Reads is { } r ? N(r) + " reads in " + Range : "", metric?.ReadTrend, "ts-cat-1");
        yield return ("Writes", metric?.Writes is { } writes ? TerraScale.Dashboard.Shared.Charts.ChartGeometry.Compact(writes) : null, metric?.Writes is { } w ? N(w) + " puts, patches and deletes in " + Range : "", metric?.WriteTrend, "ts-cat-2");
        yield return ("Storage", metric?.StorageBytes is { } bytes ? TerraScale.Dashboard.Features.Usage.UsageFormat.Bytes(bytes) : null, metric?.StorageBytes is { } b ? N(b) + " bytes in the latest bucket, summed across every reporting region" : "", null, "");
        yield return ("p95 latency", metric?.P95Ms is { } ms ? ms.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " ms" : null, "Highest region p95 in the latest bucket", null, "");
    }
}

// Totals for one database over the list range; null means TerraBase did not report it and is never shown as zero.
public sealed record DatabaseListMetric(long? Reads, long? Writes, long? StorageBytes, double? P95Ms, AnalyticsLinePlot? ReadTrend, AnalyticsLinePlot? WriteTrend);

// One project-wide analytics read for the whole list; Message says why metrics are missing.
public sealed record DatabaseListMetrics(IReadOnlyDictionary<string, DatabaseListMetric> Databases, string Message)
{
    private static readonly string[] Writes = ["put", "patch", "delete"];

    public static async Task<DatabaseListMetrics> LoadAsync(IAnalyticsBackend backend, AccountContext context, string range, CancellationToken cancellationToken)
    {
        if (context.Project is not { Databases.Count: > 0 }) return new(new Dictionary<string, DatabaseListMetric>(), "");
        try
        {
            var selection = AnalyticsSelection.For(context, "", "", "", range);
            var batch = await backend.ReadAsync(selection, cancellationToken);
            if (batch.State == AnalyticsSourceState.Unavailable) return new(new Dictionary<string, DatabaseListMetric>(), "Database metrics are unavailable.");
            if (batch.State == AnalyticsSourceState.NotMetered || batch.Points.Count == 0) return new(new Dictionary<string, DatabaseListMetric>(), "No database measurements in the last " + range + ".");
            // Build validates buckets and values the same way the analytics page does.
            _ = AnalyticsService.Build(selection, batch.Points);
            var bucket = range switch { "1h" => 60, "24h" => 3600, "7d" => 21600, _ => 86400 };
            var first = batch.Points.Min(point => point.Start.ToUniversalTime());
            var starts = Enumerable.Range(0, checked((int)((batch.Points.Max(point => point.Start.ToUniversalTime()) - first).TotalSeconds / bucket) + 1)).Select(index => first.AddSeconds((long)index * bucket)).ToArray();
            return new(batch.Points.GroupBy(point => point.DatabaseId).ToDictionary(group => group.Key, group =>
            {
                var name = selection.Context.Project!.Databases[group.Key].Name;
                AnalyticsLinePlot? Trend(string label, Func<AnalyticsPoint, long?> measure, out long? total)
                {
                    var values = new long[starts.Length]; var missing = Enumerable.Repeat(true, starts.Length).ToArray();
                    total = null;
                    foreach (var point in group)
                    {
                        if (measure(point) is not { } value) continue;
                        var index = (int)((point.Start.ToUniversalTime() - first).TotalSeconds / bucket);
                        values[index] = checked(values[index] + value); missing[index] = false;
                        total = checked((total ?? 0) + value);
                    }
                    return total is null ? null : AnalyticsLinePlot.For(name + " " + label + ", " + range, range, starts, [new AnalyticsSeries(label, values, missing)]);
                }
                var readTrend = Trend("reads", point => point.Operations.TryGetValue("read", out var count) ? count : null, out var reads);
                var writeTrend = Trend("writes", point => Writes.Any(point.Operations.ContainsKey) ? Writes.Sum(op => point.Operations.GetValueOrDefault(op)) : null, out var writes);
                // Gauges read only the latest project bucket, like the analytics page: an explicit null there stays missing and never falls back
                // to an older reading, and storage is a total only when every region this database reported from has a value in that bucket.
                var current = group.Where(point => point.Start.ToUniversalTime() == starts[^1]).ToArray();
                var storage = current.Length > 0 && current.All(point => point.StorageBytes is not null) &&
                    group.Select(point => point.Region).Distinct(StringComparer.OrdinalIgnoreCase).Count() == current.Select(point => point.Region).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                    ? current.Aggregate(0L, (sum, point) => checked(sum + point.StorageBytes!.Value)) : (long?)null;
                var p95 = current.Max(point => point.P95Ms);
                return new DatabaseListMetric(reads, writes, storage, p95, readTrend, writeTrend);
            }), "");
        }
        catch (Exception error) when (error is AccountAccessException or HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { return new(new Dictionary<string, DatabaseListMetric>(), "Could not load database metrics. Reload the page to retry."); }
    }
}

public sealed record DatabaseDetailPage(ShellModel Shell, DatabaseRecord Database, string Tab, IReadOnlyList<DatabaseRegion> Choices,
    IReadOnlyList<CollectionStats> Contents, string Error = "", string RegionsError = "", bool OpenDelete = false, bool Created = false, TerraScale.Dashboard.Features.Backups.BackupPage? Backups = null,
    bool ContentsAvailable = false, TerraScale.Dashboard.Features.Usage.UsageReplication? Measurements = null, string Range = "24h", DatabaseActivity? Activity = null)
{
    // Each activity card keeps its own range; changing one keeps the others as they are.
    public string RangePath(string key, string range) => DatabaseService.Path(Database.Id) + "?" +
        string.Join('&', Activity!.Cards.Select(card => card.Key + "=" + (card.Key == key ? range : card.Range))) + "#activity";
    public AccountContext Context => Shell.Context;
    public IReadOnlyList<DatabaseRecord> Branches => Context.Project!.Databases.Values.Where(database => database.BranchedFrom == Database.Id).OrderBy(database => database.Name, StringComparer.Ordinal).ToArray();
    // An all-regions database serves every region in TerraBase's catalog (Choices); its record names only the home.
    public IReadOnlyList<string> Serving => Database.AllRegions
        ? [.. DatabaseService.Regions(Database).Concat(Choices.Select(region => region.Code)).Distinct(StringComparer.OrdinalIgnoreCase)]
        : DatabaseService.Regions(Database);
    public string Placement => Serving.Count == 0 ? DatabasesPage.RegionDisplay("")
        : string.Join(", ", Serving.Select(DatabasesPage.RegionDisplay));
    public (string Tone, string Title, string Body) CreatedState => DatabasesPage.Tone(Database.Status) switch
    {
        "success" => ("success", Database.Name + " is ready", Serving.Count == 1 || Database.AllRegions
            ? "It serves " + Placement + ". " + (Serving.Count > 1 ? "Replica progress is shown in Replication. " : "") + "Add a collection in Documents, or connect with an API key."
            : "Its home region is ready. Replica progress is shown in Replication. Add a collection in Documents, or connect with an API key."),
        "danger" => ("danger", Database.Name + " could not be created", "The data plane did not finish creating it. Delete it and create it again, or contact support if it keeps failing."),
        _ => ("info", Database.Name + " is being created", "The data plane is still placing it in " + Placement + ". Its status changes to ready when it can take requests.")
    };
    public IReadOnlyList<string> PlacementLabels
    {
        get
        {
            var configured = Serving;
            var labels = configured.Select(DatabasesPage.RegionDisplay).ToList();
            if (Context.Project?.BackupRegion is { Length: > 0 } backup && !configured.Contains(backup, StringComparer.OrdinalIgnoreCase))
                labels.Add(DatabasesPage.RegionDisplay(backup) + " · backup region");
            return labels;
        }
    }
    public string Replication => Serving switch
    {
        { Count: 0 } => "Not reported",
        { Count: 1 } regions => "Single region · " + regions[0].ToUpperInvariant() + " only",
        var regions => "Replicated across " + string.Join(", ", regions).ToUpperInvariant()
    };
}

// One activity card with its own range; Plot is null when Message says why there is no chart.
public sealed record DatabaseActivityCard(string Key, string Title, string Range, AnalyticsLinePlot? Plot, string Message, bool Failed = false);

// Per-region activity for one database, read from the same TerraBase analytics route as /analytics, once per distinct card range.
public sealed record DatabaseActivity(IReadOnlyList<DatabaseActivityCard> Cards, IReadOnlyList<AnalyticsRegionRow> Regions)
{
    public static readonly (string Key, string Title)[] Kinds = [("reads", "Reads"), ("writes", "Writes (put, patch, delete)"), ("storage", "Storage (bytes)"), ("latency", "p95 latency (µs)")];
    // TerraBase meters committed document operations as read, put, patch and delete.
    private static readonly string[] WriteOperations = ["put", "patch", "delete"];

    // rangeFor returns an already normalized range for each card key.
    public static async Task<DatabaseActivity> LoadAsync(IAnalyticsBackend backend, AccountContext context, string databaseId, Func<string, string> rangeFor, CancellationToken cancellationToken)
    {
        var cards = Kinds.Select(kind => new DatabaseActivityCard(kind.Key, kind.Title, rangeFor(kind.Key), null, "")).ToArray();
        IReadOnlyList<AnalyticsRegionRow> latencyRegions = [];
        // Each distinct range is one analytics read; they run together and each updates only its own cards.
        await Task.WhenAll(cards.Select(card => card.Range).Distinct().ToArray().Select(async range =>
        {
            void Set(Func<DatabaseActivityCard, DatabaseActivityCard> update) { for (var i = 0; i < cards.Length; i++) if (cards[i].Range == range) cards[i] = update(cards[i]); }
            try
            {
                var selection = AnalyticsSelection.For(context, "", databaseId, "", range);
                var batch = await backend.ReadAsync(selection, cancellationToken);
                if (batch.State == AnalyticsSourceState.Unavailable) { Set(card => card with { Message = "Activity measurements are unavailable." }); return; }
                if (batch.State == AnalyticsSourceState.NotMetered || batch.Points.Count == 0) { Set(card => card with { Message = "No activity measurements for this database in the last " + range + "." }); return; }
                // Build validates buckets and values the same way the analytics page does; the backend already bounds buckets to the reporting window.
                var regions = AnalyticsService.Build(selection, batch.Points).Totals.Regions;
                if (cards.Any(card => card.Key == "latency" && card.Range == range)) latencyRegions = regions;
                var bucket = range switch { "1h" => 60, "24h" => 3600, "7d" => 21600, _ => 86400 };
                var first = batch.Points.Min(point => point.Start.ToUniversalTime());
                var last = batch.Points.Max(point => point.Start.ToUniversalTime());
                var starts = Enumerable.Range(0, checked((int)((last - first).TotalSeconds / bucket) + 1)).Select(index => first.AddSeconds((long)index * bucket)).ToArray();
                var plots = cards.Where(card => card.Range == range).ToDictionary(card => card.Key, card => AnalyticsLinePlot.For(card.Title + ", " + range, range, starts, regions.Select(region =>
                {
                    var values = new long[starts.Length];
                    var missing = Enumerable.Repeat(true, starts.Length).ToArray();
                    foreach (var point in batch.Points.Where(sample => sample.Region.Equals(region.Code, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (Measure(card.Key, point) is not { } value) continue;
                        var index = (int)((point.Start.ToUniversalTime() - first).TotalSeconds / bucket);
                        // Counters add up within a bucket; gauges (storage, p95) keep the highest observation and are never summed.
                        values[index] = missing[index] ? value : card.Key is "reads" or "writes" ? checked(values[index] + value) : Math.Max(values[index], value);
                        missing[index] = false;
                    }
                    return new AnalyticsSeries(region.Code, values, missing);
                }).Where(series => series.Missing!.Contains(false)).ToArray()));
                Set(card => card with { Plot = plots[card.Key], Message = "No " + card.Title.Split(' ')[0].ToLowerInvariant() + " samples in the last " + range + "." });
            }
            catch (Exception error) when (error is AccountAccessException or HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException ||
                error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { Set(card => card with { Plot = null, Message = "Could not load activity measurements. Reload the page to retry.", Failed = true }); }
        }));
        return new(cards, latencyRegions);
    }

    // Missing operation names stay missing; they are never reported as zero.
    private static long? Measure(string key, AnalyticsPoint point) => key switch
    {
        "reads" => point.Operations.TryGetValue("read", out var reads) ? (long?)reads : null,
        "writes" => WriteOperations.Any(point.Operations.ContainsKey) ? (long?)WriteOperations.Sum(name => point.Operations.GetValueOrDefault(name)) : null,
        "storage" => point.StorageBytes,
        _ => point.P95Ms is { } ms ? checked((long)Math.Round(ms * 1000, MidpointRounding.AwayFromZero)) : null,
    };
}

public sealed record DatabaseReplicationModel(TerraScale.Dashboard.Features.Usage.UsageReplication Reading, string ProjectId, string DatabaseId, string Range, DatabaseRecord? Database = null, DatabaseActivity? Activity = null)
{
    // Map hubs exist only where overlays.css positions a node; other regions stay in the cards and tables.
    public static readonly IReadOnlyDictionary<string, (double X, double Y)> Hubs = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase) { ["gru"] = (50.00, 59.76), ["fra"] = (71.16, 20.32), ["sjc"] = (21.21, 28.83), ["sfo"] = (21.00, 28.58) };
    public IReadOnlyList<TerraScale.Dashboard.Features.Usage.UsageReplica> Replicas => Reading.Regions.Where(row => row.DatabaseId == DatabaseId).OrderBy(row => Regions.Rank(row.Region)).ToArray();
    public static string Tone(string status) => status switch { "healthy" or "operational" => "success", "lagging" or "degraded" => "warning", "diverged" or "stopped" or "down" => "danger", _ => "neutral" };
    public string Arc(string to)
    {
        var (x1, y1) = Hubs[Database!.Region]; var (x2, y2) = Hubs[to];
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"M {x1:0.00} {y1:0.00} Q {(x1 + x2) / 2:0.00} {Math.Min(y1, y2) - 12:0.00} {x2:0.00} {y2:0.00}");
    }
    public double? P95(string region) => Activity?.Regions.FirstOrDefault(row => row.Code.Equals(region, StringComparison.OrdinalIgnoreCase))?.LatestP95Ms;
    public string AnalyticsPath => "/analytics?database=" + Uri.EscapeDataString(DatabaseId) + "&project=" + Uri.EscapeDataString(ProjectId) + "&range=" + Range;
}
