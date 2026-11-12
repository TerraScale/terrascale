using System.Diagnostics.Metrics;
using TerraBase.Errors;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Platform;

internal static class PlatformDatabases
{
    public const string OrganizationId = "00000000000000000000000001";

    public const string AuthProjectId = "00000000000000000000000002";

    public const string DashboardProjectId = "00000000000000000000000005";

    public const string AuthId = "00000000000000000000000003";

    public const string DashboardId = "00000000000000000000000004";

    public const string Auth = "ts-auth";

    public const string Dashboard = "ts-dashboard";

    public static IReadOnlyDictionary<string, (string Id, string ProjectId)> Databases { get; } = new Dictionary<string, (string Id, string ProjectId)>(StringComparer.Ordinal)
    {
        [Auth] = (AuthId, AuthProjectId),
        [Dashboard] = (DashboardId, DashboardProjectId),
    };

    public static readonly Conflict Orphaned = new(
        "a platform database file has no catalog record; the file audit deletes it 24 hours after it first sees it", "platform_database_orphaned");

    public static bool IsPlatform(string? organizationId) => organizationId == OrganizationId;

    public static bool IsPlatformProject(string? projectId) => projectId is AuthProjectId or DashboardProjectId;

    public static bool IsPlatformDatabase(string? databaseId) => databaseId is AuthId or DashboardId;

    public static bool IsPlatformName(string projectId, string name) => Databases.TryGetValue(name, out var database) && database.ProjectId == projectId;

    public static DatabasePlacement Placement(string region) => new(region, [], 1);

    public static IServiceCollection AddTerraBasePlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<PlatformSeedProgress>();
        services.AddHostedService<PlatformSeed>();
        return services;
    }
}

internal sealed class PlatformSeedProgress
{
    private volatile bool _ready;
    private volatile string? _pending;

    public PlatformSeedProgress(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        meters.Create(Telemetry.MeterName).CreateObservableGauge("terrabase.platform.seed_pending", Observe,
            description: "One, labelled with its reason, while the platform database seed is still trying.");
    }

    internal bool Ready => _ready;

    internal void Wait(string reason) => _pending = reason;

    internal void Serve(string reason)
    {
        _pending = reason;
        _ready = true;
    }

    internal void Finish()
    {
        _ready = true;
        _pending = null;
    }

    private IEnumerable<Measurement<int>> Observe() =>
        _pending is { } reason ? [new Measurement<int>(1, new KeyValuePair<string, object?>("reason", reason))] : [];
}

public sealed record PlatformConflicts(IReadOnlyList<Conflict> Conflicts);

public sealed record PlatformPending(IReadOnlyList<Conflict> Conflicts, Unavailable Reason);

public sealed record PlatformOrphaned(IReadOnlyList<Conflict> Conflicts);

public readonly union EnsurePlatformResult(Done, PlatformConflicts, PlatformPending, PlatformOrphaned);

public interface IPlatformCatalog
{
    ValueTask<EnsurePlatformResult> EnsurePlatformAsync(CancellationToken cancellationToken);
}

internal sealed class PlatformSeed(IPlatformCatalog catalog, ICatalogGate gate, PlatformSeedProgress progress, TimeProvider time, ILogger<PlatformSeed> logger) : BackgroundService
{
    private const string CatalogUnverified = "catalog_unverified";
    private const string Seeding = "seeding";
    private const string Failed = "internal_error";
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!gate.IsOpen)
        {
            progress.Wait(CatalogUnverified);
            logger.ZLogWarning($"Platform databases not seeded yet: this machine's catalog has not passed the restore gate; waiting for it");
            await gate.Opened.WaitAsync(stoppingToken).ConfigureAwait(false);
        }

        HashSet<string> reported = [with(StringComparer.Ordinal)];
        string? failure = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!progress.Ready)
            {
                progress.Wait(Seeding);
            }

            try
            {
                switch (await catalog.EnsurePlatformAsync(stoppingToken).ConfigureAwait(false))
                {
                    case Done:
                        progress.Finish();
                        logger.ZLogInformation($"Platform databases are seeded");
                        return;
                    case PlatformConflicts settled:
                        Report(settled.Conflicts, reported);
                        progress.Finish();
                        return;
                    case PlatformOrphaned orphaned:
                        Report(orphaned.Conflicts, reported);
                        progress.Serve(PlatformDatabases.Orphaned.Code);
                        break;
                    case PlatformPending pending:
                        Report(pending.Conflicts, reported);
                        progress.Wait(pending.Reason.Code);
                        if (failure != pending.Reason.Message)
                        {
                            failure = pending.Reason.Message;
                            logger.ZLogWarning($"Platform databases not seeded yet: {pending.Reason.Message:@Reason}; retrying");
                        }

                        break;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                progress.Wait(Failed);
                if (failure != error.GetType().Name)
                {
                    failure = error.GetType().Name;
                    logger.ZLogWarning(error, $"Platform databases not seeded yet; retrying");
                }
            }

            await Task.Delay(Retry, time, stoppingToken).ConfigureAwait(false);
        }
    }

    private void Report(IReadOnlyList<Conflict> conflicts, HashSet<string> reported)
    {
        foreach (var conflict in conflicts)
        {
            if (reported.Add(conflict.Code))
            {
                logger.ZLogError($"Platform databases conflict with existing records: {conflict.Code:@Code}; {conflict.Message:@Reason}");
            }
        }
    }
}
