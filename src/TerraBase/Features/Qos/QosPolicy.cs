using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

/// <summary>Node-local resource limits applied to an organization's trusted plan.</summary>
/// <param name="RequestsPerSecond">Token replenishment rate; zero disables the plan limit.</param>
/// <param name="Burst">Maximum tokens; zero disables the plan limit.</param>
/// <param name="Concurrency">Simultaneous requests; zero disables the plan limit.</param>
/// <param name="Weight">Relative SQLite scheduling weight.</param>
/// <param name="MemoryShare">Maximum fraction of the process budget reserved by one organization.</param>
/// <param name="CacheShare">Maximum fraction of the document cache held by one organization.</param>
/// <param name="QueryTimeout">SQLite execution time allowed, excluding queue and consumer waits.</param>
/// <param name="QuerySteps">SQLite virtual machine instruction limit.</param>
/// <param name="ScanRows">Maximum candidate document rows scanned.</param>
/// <param name="ScanBytes">Maximum candidate JSON bytes scanned.</param>
/// <param name="StorageBytes">Maximum live document bytes per organization.</param>
public sealed record QosPolicy(
    int RequestsPerSecond, int Burst, int Concurrency, int Weight, double MemoryShare, double CacheShare,
    TimeSpan QueryTimeout, long QuerySteps, long ScanRows, long ScanBytes, long StorageBytes)
{
    private static readonly QosPolicy Hobby = new(50, 100, 4, 1, .0625, .0625, TimeSpan.FromSeconds(1), 1_000_000, 10_000, 16 * 1024 * 1024, 1024L * 1024 * 1024);
    private static readonly QosPolicy Pro = new(500, 1000, 16, 4, .25, .25, TimeSpan.FromSeconds(5), 5_000_000, 100_000, 128 * 1024 * 1024, 100L * 1024 * 1024 * 1024);
    private static readonly QosPolicy Enterprise = new(0, 0, 0, 16, 1, 1, Timeout.InfiniteTimeSpan, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue);

    /// <summary>Limits for a validated plan; enterprise retains only process-wide safety bounds.</summary>
    public static QosPolicy For(PlanTier plan) => plan switch
    {
        PlanTier.Hobby => Hobby,
        PlanTier.Pro => Pro,
        PlanTier.Enterprise => Enterprise,
        _ => throw new ArgumentOutOfRangeException(nameof(plan)),
    };
}
