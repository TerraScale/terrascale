using TerraBase.Tenancy;

namespace TerraBase.Features.Stats;

/// <summary>
/// Counters of one scope over one closed window. Latency bins follow <see cref="StatsLatency.BoundsMs"/>, and are null when
/// there were no requests.
/// </summary>
internal sealed record StatsCounters(long Reads, long Writes, long Requests, long Errors, long SentBytes, long ReceivedBytes,
    long LagGroups, double LagSeconds, long Conflicts, long[]? Latency, long? StorageBytes);

/// <summary>One peer region's replication over one closed window: bytes moved, and the latest lag and divergence.</summary>
internal sealed record PeerCounters(string Region, long SentBytes, long ReceivedBytes, long LagGroups, double LagSeconds, long Conflicts);

/// <summary>Reads and writes of one collection over one closed window.</summary>
internal sealed record CollectionWindow(string Name, long Reads, long Writes);

/// <summary>One database's counters over one closed window, with the peers and collections active in it.</summary>
internal sealed record DatabaseWindow(TenantContext Database, StatsCounters Counters, IReadOnlyList<PeerCounters> Peers,
    IReadOnlyList<CollectionWindow> Collections);

/// <summary>Bytes replicated with one peer region since this process started.</summary>
internal sealed record RegionTotal(string Region, long SentBytes, long ReceivedBytes);

/// <summary>
/// One closed window: the member's totals and peer regions, every database with activity or a storage sample, and the
/// cumulative counts since start. Rates divide by <see cref="Duration"/>, the time the window really covered.
/// </summary>
internal sealed record StatsWindow(DateTimeOffset At, TimeSpan Duration, int Databases, StatsCounters Totals, IReadOnlyList<PeerCounters> Peers,
    IReadOnlyList<DatabaseWindow> Active, IReadOnlyList<RegionTotal> Cumulative, long ReadsSinceStart, long WritesSinceStart);
