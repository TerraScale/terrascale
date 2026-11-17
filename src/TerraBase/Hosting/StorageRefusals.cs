using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TerraBase.Engine;
using ZLogger;

namespace TerraBase.Hosting;

internal sealed class StorageRefusals(IMeterFactory meters, ILogger<StorageRefusals> logger)
{
    private const int MaxDatabases = 10_000;
    private readonly ConcurrentDictionary<string, string> _sites = new(StringComparer.Ordinal);
    private readonly Counter<long> _answers = meters.Create(Telemetry.MeterName)
        .CreateCounter<long>("terrabase.storage.busy_answers", "{answer}", "Requests answered busy because storage refused them, by answer and refusing source.");

    internal void Answered(string? database, string answer, StorageBusyException busy)
    {
        _answers.Add(1, new KeyValuePair<string, object?>("answer", answer), new KeyValuePair<string, object?>("at", busy.At));
        var key = database ?? "";
        if (_sites.TryGetValue(key, out var site) && site == busy.At)
        {
            return;
        }

        if (_sites.Count >= MaxDatabases)
        {
            _sites.Clear();
        }

        _sites[key] = busy.At;
        logger.ZLogWarning($"Database {database:@Database} answered {answer:@Answer}: storage refused at {busy.At:@At}");
    }
}
