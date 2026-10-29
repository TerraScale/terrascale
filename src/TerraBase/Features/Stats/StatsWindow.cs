using TerraBase.Tenancy;

namespace TerraBase.Features.Stats;

internal sealed record StatsCounters(long Reads, long Writes, long Requests, long Errors, long[]? Latency, long? StorageBytes);

internal sealed record CollectionWindow(string Name, long Reads, long Writes);

internal sealed record DatabaseWindow(TenantContext Database, StatsCounters Counters, IReadOnlyList<CollectionWindow> Collections);

internal sealed record StatsWindow(DateTimeOffset At, TimeSpan Duration, int Databases, StatsCounters Totals,
    IReadOnlyList<DatabaseWindow> Active, long ReadsSinceStart, long WritesSinceStart);
