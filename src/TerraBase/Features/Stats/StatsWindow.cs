using TerraBase.Tenancy;

namespace TerraBase.Features.Stats;

internal sealed record StatsCounters(long Reads, long Writes, long Requests, long Errors, long[]? Latency, long? StorageBytes);

internal sealed record DatabaseWindow(TenantContext Database, StatsCounters Counters);

internal sealed record StatsWindow(DateTimeOffset At, TimeSpan Duration, int Databases, StatsCounters Totals,
    IReadOnlyList<DatabaseWindow> Active, long ReadsSinceStart, long WritesSinceStart);
