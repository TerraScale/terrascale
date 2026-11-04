using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

/// <summary>Node-local resource limits applied to an organization's trusted plan.</summary>
/// <param name="RequestsPerSecond">Token replenishment rate; zero disables the plan limit.</param>
/// <param name="Burst">Maximum tokens; zero disables the plan limit.</param>
/// <param name="Concurrency">Simultaneous requests; zero disables the plan limit.</param>
/// <param name="Weight">Relative Turso scheduling weight.</param>
/// <param name="MemoryShare">Maximum fraction of the process budget reserved by one organization.</param>
/// <param name="CacheShare">Maximum fraction of the read cache held by one organization.</param>
/// <param name="QueryTimeout">Turso execution time allowed, excluding queue and consumer waits.</param>
/// <param name="QueryWork">Engine work limit, in units scaled like SQLite virtual machine instructions.</param>
/// <param name="ScanRows">Maximum candidate rows scanned.</param>
/// <param name="ScanBytes">Maximum candidate JSON bytes scanned.</param>
/// <param name="StorageBytes">Maximum stored bytes per organization, in decimal GB as pricing.json entitlements.storageLimitGb publishes it.</param>
/// <param name="Databases">Live databases per organization, across its projects, before new ones are refused (https://terrascale.tech/pricing.json).</param>
/// <param name="MonthlyWriteCap">Write units per organization and UTC month after which new writes stop: the hard cap of pricing.json limits.operationUnitCapPerMonth.</param>
/// <param name="MonthlySqlReads">SQL read units per organization and UTC month after which SQL reads stop: the fair use of pricing.json limits.sqlReadUnitsPerMonth. Shape reads are never counted.</param>
/// <param name="EngineSeconds">SQL statement execution seconds replenished per second; zero disables the plan limit.</param>
/// <param name="EngineBurst">Maximum SQL statement execution seconds an organization can spend at once.</param>
public sealed record QosPolicy(
    int RequestsPerSecond, int Burst, int Concurrency, int Weight, double MemoryShare, double CacheShare,
    TimeSpan QueryTimeout, long QueryWork, long ScanRows, long ScanBytes, long StorageBytes, long Databases,
    long MonthlyWriteCap, long MonthlySqlReads, double EngineSeconds, double EngineBurst)
{
    private static readonly QosPolicy Hobby = new(50, 100, 4, 1, .0625, .0625, TimeSpan.FromSeconds(1), 1_000_000, 10_000, 16 * 1024 * 1024, 100_000_000, 1, Cap(PlanTier.Hobby), Reads(PlanTier.Hobby), .5, 3);
    private static readonly QosPolicy Starter = new(200, 400, 8, 2, .125, .125, TimeSpan.FromSeconds(2), 2_000_000, 50_000, 64 * 1024 * 1024, 5_000_000_000, 5, Cap(PlanTier.Starter), Reads(PlanTier.Starter), 1, 6);
    private static readonly QosPolicy Pro = new(500, 1000, 16, 4, .25, .25, TimeSpan.FromSeconds(5), 5_000_000, 100_000, 128 * 1024 * 1024, 100_000_000_000, 25, Cap(PlanTier.Pro), Reads(PlanTier.Pro), 2, 15);
    private static readonly QosPolicy Team = new(500, 1000, 16, 4, .25, .25, TimeSpan.FromSeconds(5), 5_000_000, 100_000, 128 * 1024 * 1024, 100_000_000_000, 100, Cap(PlanTier.Team), Reads(PlanTier.Team), 2, 15);
    private static readonly QosPolicy Enterprise = new(0, 0, 0, 16, 1, 1, Timeout.InfiniteTimeSpan, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, Cap(PlanTier.Enterprise), Reads(PlanTier.Enterprise), 0, 0);

    private static long Cap(PlanTier plan) => PlanPricing.Ceiling(plan, "operationUnitCapPerMonth");

    private static long Reads(PlanTier plan) => PlanPricing.Ceiling(plan, "sqlReadUnitsPerMonth");

    /// <summary>Limits for a validated plan; enterprise retains only process-wide safety bounds.</summary>
    public static QosPolicy For(PlanTier plan) => plan switch
    {
        PlanTier.Hobby => Hobby,
        PlanTier.Starter => Starter,
        PlanTier.Pro => Pro,
        PlanTier.Team => Team,
        PlanTier.Enterprise => Enterprise,
        _ => throw new ArgumentOutOfRangeException(nameof(plan)),
    };
}
