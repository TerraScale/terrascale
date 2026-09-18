using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Usage;

/// <summary>GET /usage/export — CSV of the current cycle usage counters and
/// per-database rows (parity with the original usage export action).</summary>
public sealed class ExportUsageCsvEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/usage/export");
        Summary("Exports current-cycle usage as CSV");
        Tags("Usage");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var usage = store.Usage;
        var lines = new List<string>
        {
            "metric,used,included",
            $"storage_bytes,{store.StorageBytesTotal},{store.StorageLimitBytes}",
            $"storage_percent,{store.StoragePercentUsed.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)},100",
            $"writes,{usage.WriteUnits},",
            $"reads_unbilled,{usage.ReadUnits},",
            $"requests,{usage.Requests},{usage.RequestLimit}",
        };

        lines.Add(string.Empty);
        lines.Add("database,regions,documents,storage_bytes,created_at");
        foreach (var database in store.ProjectDatabases(store.ActiveProjectId))
        {
            var regions = database.PrimaryRegion + (database.BackupRegion is null ? "" : "|" + database.BackupRegion);
            lines.Add($"{database.Name},{regions},{database.DocumentCount},{database.SizeMb * 1024L * 1024},{database.CreatedAt}");
        }

        var csv = string.Join("\n", lines) + "\n";
        return Task.FromResult<IResult>(Results.Text(csv, "text/csv", System.Text.Encoding.UTF8));
    }
}
