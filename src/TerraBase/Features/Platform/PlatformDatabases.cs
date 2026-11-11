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

    public static bool IsPlatform(string? organizationId) => organizationId == OrganizationId;

    public static bool IsPlatformProject(string? projectId) => projectId is AuthProjectId or DashboardProjectId;

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
    private volatile bool _finished;
    private volatile string? _pending;

    public PlatformSeedProgress(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        meters.Create(Telemetry.MeterName).CreateObservableGauge("terrabase.platform.seed_pending", Observe,
            description: "One, labelled with its reason, while the platform database seed holds readiness.");
    }

    internal bool Finished => _finished;

    internal void Wait(string reason) => _pending = reason;

    internal void Finish()
    {
        _finished = true;
        _pending = null;
    }

    private IEnumerable<Measurement<int>> Observe() =>
        _pending is { } reason ? [new Measurement<int>(1, new KeyValuePair<string, object?>("reason", reason))] : [];
}

public readonly union EnsurePlatformResult(Done, Conflict, Unavailable);

public interface IPlatformCatalog
{
    ValueTask<EnsurePlatformResult> EnsurePlatformAsync(CancellationToken cancellationToken);
}

internal sealed class PlatformSeed(IPlatformCatalog catalog, ICatalogGate gate, PlatformSeedProgress progress, TimeProvider time, ILogger<PlatformSeed> logger) : BackgroundService
{
    private const string CatalogUnverified = "catalog_unverified";
    private const string Failed = "internal_error";
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!gate.IsOpen)
        {
            progress.Wait(CatalogUnverified);
            await gate.Opened.WaitAsync(stoppingToken).ConfigureAwait(false);
        }

        string? failure = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                switch (await catalog.EnsurePlatformAsync(stoppingToken).ConfigureAwait(false))
                {
                    case Done:
                        progress.Finish();
                        logger.ZLogInformation($"Platform databases are seeded");
                        return;
                    case Conflict conflict:
                        progress.Finish();
                        logger.ZLogError($"Platform databases conflict with existing catalog records: {conflict.Code:@Code}");
                        return;
                    case Unavailable unavailable:
                        progress.Wait(unavailable.Code);
                        if (failure != unavailable.Message)
                        {
                            failure = unavailable.Message;
                            logger.ZLogWarning($"Platform databases not seeded yet: {unavailable.Message:@Reason}; retrying");
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
}
