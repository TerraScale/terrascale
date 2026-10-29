using System.Diagnostics.Metrics;
using TerraBase.Hosting;

namespace TerraBase.Features.Stats;
internal sealed class StatsMetrics
{
    private const int TopCount = 10;

    public StatsMetrics(IMeterFactory meters, DatabaseStats stats)
    {
        var meter = meters.Create(Telemetry.MeterName);
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
        meter.CreateObservableGauge<double>("terrabase.stats.top.operations", () => Ranked(stats.LastWindow,
            database => database.Counters.Reads + database.Counters.Writes), "{operation}/s",
            "Document operations per second of the busiest databases in the last window.");
    }


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
