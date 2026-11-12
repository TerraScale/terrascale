using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replicas;

internal sealed class RemovedReplicaFaults(IMeterFactory meters, TimeProvider time, ILogger<RemovedReplicaFaults> logger)
{
    private const string NoAnswer = "unanswered";
    private static readonly TimeSpan LogEvery = TimeSpan.FromMinutes(10);
    private readonly Counter<long> _errors = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.replica.removed_record_errors");
    private readonly ConcurrentDictionary<(string Region, string Error), DateTimeOffset> _logged = new();

    public void Unanswered(TenantContext database, string home)
    {
        if (Counted(home, NoAnswer))
        {
            logger.ZLogWarning($"Region {home:@Region} did not answer for the placement of database {database.DatabaseId:@Database}, which it no longer places here; the next pass asks again");
        }
    }

    public void Rejected(TenantContext database, string home)
    {
        if (Counted(home, "catalog_rejected"))
        {
            logger.ZLogWarning($"Region {home:@Region}'s catalog entry for database {database.DatabaseId:@Database} could not be adopted or its name was taken; the stored routing record is retained");
        }
    }

    public void Failed(TenantContext database, string home, Exception error)
    {
        if (Counted(home, error.GetType().Name))
        {
            logger.ZLogWarning(error, $"The record of database {database.DatabaseId:@Database}, which region {home:@Region} no longer places here, could not be brought up to date; the next pass tries again");
        }
    }

    private bool Counted(string home, string error)
    {
        _errors.Add(1, new TagList { { "region", home }, { "error", error } });
        var now = time.GetUtcNow();
        if (_logged.TryGetValue((home, error), out var last) && now - last < LogEvery)
        {
            return false;
        }

        _logged[(home, error)] = now;
        return true;
    }
}
