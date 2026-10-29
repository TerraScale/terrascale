using TerraBase.Errors;
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

    public static DatabasePlacement Placement(string region) => new(region, [], 1);

    public static IServiceCollection AddTerraBasePlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<PlatformSeed>();
        return services;
    }
}

public readonly union EnsurePlatformResult(Done, Conflict, Unavailable);

public interface IPlatformCatalog
{
    ValueTask<EnsurePlatformResult> EnsurePlatformAsync(CancellationToken cancellationToken);
}

internal sealed class PlatformSeed(IPlatformCatalog catalog, TimeProvider time, ILogger<PlatformSeed> logger) : BackgroundService
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? failure = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                switch (await catalog.EnsurePlatformAsync(stoppingToken).ConfigureAwait(false))
                {
                    case Done:
                        logger.ZLogInformation($"Platform databases are seeded");
                        return;
                    case Conflict conflict:
                        logger.ZLogError($"Platform databases conflict with existing catalog records: {conflict.Code:@Code}");
                        return;
                    case Unavailable unavailable when failure != unavailable.Message:
                        failure = unavailable.Message;
                        logger.ZLogWarning($"Platform databases not seeded yet: {unavailable.Message:@Reason}; retrying");
                        break;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
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
