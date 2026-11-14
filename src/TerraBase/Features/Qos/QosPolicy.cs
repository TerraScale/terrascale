using TerraBase.Tenancy;
using TerraBase.Features.Platform;

namespace TerraBase.Features.Qos;

public sealed record QosPolicy(
    int RequestsPerSecond, int Burst, int Concurrency, int Weight, double MemoryShare, double CacheShare,
    TimeSpan QueryTimeout, long QueryWork, long ScanRows, long ScanBytes, long StorageBytes, long Databases,
    long MonthlyWriteCap, long MonthlySqlReads, double EngineSeconds, double EngineBurst)
{
    private static readonly QosPolicy Hobby = new(50, 100, 4, 1, .0625, .0625, TimeSpan.FromSeconds(1), 1_000_000, 10_000, 16 * 1024 * 1024, 100_000_000, 1, Cap(PlanTier.Hobby), Reads(PlanTier.Hobby), .5, 3);
    private static readonly QosPolicy Starter = new(200, 400, 8, 2, .125, .125, TimeSpan.FromSeconds(2), 2_000_000, 50_000, 64 * 1024 * 1024, 5_000_000_000, 5, Cap(PlanTier.Starter), Reads(PlanTier.Starter), 1, 6);
    private static readonly QosPolicy Pro = new(500, 1000, 16, 4, .25, .25, TimeSpan.FromSeconds(5), 5_000_000, 100_000, 128 * 1024 * 1024, 100_000_000_000, 25, Cap(PlanTier.Pro), Reads(PlanTier.Pro), 2, 15);
    private static readonly QosPolicy Team = new(500, 1000, 16, 4, .25, .25, TimeSpan.FromSeconds(5), 5_000_000, 100_000, 128 * 1024 * 1024, 100_000_000_000, 100, Cap(PlanTier.Team), Reads(PlanTier.Team), 2, 15);
    private static readonly QosPolicy Enterprise = new(2000, 4000, 64, 16, 1, 1, TimeSpan.FromSeconds(5), 20_000_000, 400_000, 128 * 1024 * 1024, 1_000_000_000_000, 1000, 100_000_000, 1_000_000_000, 8, 60);

    private static readonly QosPolicy Platform = new(0, 0, 0, 16, 1, 1, Timeout.InfiniteTimeSpan, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, Cap(PlanTier.Enterprise), Reads(PlanTier.Enterprise), 0, 0);

    public static QosPolicy For(TenantContext tenant) => For(tenant.OrganizationId, tenant.Plan);

    internal static QosPolicy For(string organization, PlanTier plan) => PlatformDatabases.IsPlatform(organization) ? Platform : For(plan);

    public int Sessions => Concurrency * 2;

    private static long Cap(PlanTier plan) => PlanPricing.Ceiling(plan, "operationUnitCapPerMonth");

    private static long Reads(PlanTier plan) => PlanPricing.Ceiling(plan, "sqlReadUnitsPerMonth");

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
