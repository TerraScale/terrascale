using TerraBase.Features.Catalog;

namespace TerraBase.Features.Qos;

internal sealed record QosPolicy(int RequestsPerSecond, int Burst, int Concurrency)
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan MaxTransaction = TimeSpan.FromSeconds(5);

    public const int MinimumMessageBytesPerSecond = 16 * 1024;

    private static readonly QosPolicy Hobby = new(50, 100, 4);

    private static readonly QosPolicy Starter = new(200, 400, 8);

    private static readonly QosPolicy Pro = new(500, 1000, 16);

    private static readonly QosPolicy Enterprise = new(2000, 4000, 64);

    private static readonly QosPolicy Unowned = new(200, 400, 32);

    private static readonly QosPolicy Anonymous = new(500, 1000, 64);

    public int Sessions => Concurrency * 2;

    public static QosPolicy For(string plan) => plan switch
    {
        "starter" => Starter,
        "pro" or "team" => Pro,
        "enterprise" or Plans.TerraScale => Enterprise,
        Tenant.UnownedPlan => Unowned,
        Tenant.AnonymousPlan => Anonymous,
        _ => Hobby,
    };

    public static TimeSpan MessageBudget(long bytes) => MessageTimeout + TimeSpan.FromSeconds((double)bytes / MinimumMessageBytesPerSecond);
}

internal sealed record Tenant(Ulid OrganizationId, string Plan)
{
    public const string UnownedPlan = "unowned";

    public const string AnonymousPlan = "anonymous";

    public static Tenant Unowned { get; } = new(Ulid.Empty, UnownedPlan);

    public static Tenant Anonymous { get; } = new(Ulid.MaxValue, AnonymousPlan);

    public QosPolicy Policy => QosPolicy.For(Plan);

    public int Bucket
    {
        get
        {
            var hash = 2166136261u;
            foreach (var character in OrganizationId.ToString())
            {
                hash = unchecked((hash ^ character) * 16777619u);
            }

            return (int)(hash & 63);
        }
    }
}

internal sealed record TenantBusy(string Reason, TimeSpan RetryAfter);
