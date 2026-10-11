using System.Diagnostics.Metrics;
using TerraBase.Hosting;

namespace TerraBase.Features.Stats;

/// <summary>Exports live statistics on the TerraBase meter. Labels are bounded: ranks, operation kinds, directions and peer regions.</summary>
internal sealed class StatsMetrics
{
    private const int TopCount = 10;

    public StatsMetrics(IMeterFactory meters, DatabaseStats stats)
    {
        var meter = meters.Create(Telemetry.MeterName);
        Rollups = meter.CreateCounter<long>("terrabase.stats.rollups", "{rollup}", "Live statistics rollups by result: published, failed or no_cell.");
        meter.CreateObservableGauge<long>("terrabase.stats.databases", () => stats.LastWindow?.Databases ?? 0,
            "{database}", "Databases tracked by live statistics.");
        meter.CreateObservableCounter<long>("terrabase.stats.operations", () =>
        {
            var window = stats.LastWindow;
            return
            [
                new Measurement<long>(window?.ReadsSinceStart ?? 0, new KeyValuePair<string, object?>("kind", "read")),
                new Measurement<long>(window?.WritesSinceStart ?? 0, new KeyValuePair<string, object?>("kind", "write")),
            ];
        }, "{operation}", "Document operations since start, counted per closed window.");
        meter.CreateObservableCounter<long>("terrabase.stats.replication", () => Replication(stats.LastWindow), "By",
            "Replication bytes with each peer region since start, counted per closed window.");
        meter.CreateObservableGauge<double>("terrabase.stats.top.replication", () => Ranked(stats.LastWindow,
            database => database.Counters.SentBytes + database.Counters.ReceivedBytes), "By/s",
            "Replication bytes per second of the busiest databases in the last window.");
        meter.CreateObservableGauge<double>("terrabase.stats.top.operations", () => Ranked(stats.LastWindow,
            database => database.Counters.Reads + database.Counters.Writes), "{operation}/s",
            "Document operations per second of the busiest databases in the last window.");
    }

    /// <summary>Rollups of closed windows, by <c>result</c>.</summary>
    public Counter<long> Rollups { get; }

    private static IEnumerable<Measurement<long>> Replication(StatsWindow? window)
    {
        foreach (var region in window?.Cumulative ?? [])
        {
            yield return new(region.SentBytes, new KeyValuePair<string, object?>("peer_region", region.Region),
                new KeyValuePair<string, object?>("direction", "sent"));
            yield return new(region.ReceivedBytes, new KeyValuePair<string, object?>("peer_region", region.Region),
                new KeyValuePair<string, object?>("direction", "received"));
        }
    }

    // Always TopCount ranks, so an idle member reports zeros rather than missing series.
    private static IEnumerable<Measurement<double>> Ranked(StatsWindow? window, Func<DatabaseWindow, long> amount)
    {
        var busiest = window?.Active.OrderByDescending(amount).Take(TopCount).ToArray() ?? [];
        var seconds = window?.Duration.TotalSeconds ?? 0;
        for (var rank = 0; rank < TopCount; rank++)
        {
            var rate = rank < busiest.Length && seconds > 0 ? amount(busiest[rank]) / seconds : 0;
            yield return new(rate, new KeyValuePair<string, object?>("rank", rank + 1));
        }
    }
}
