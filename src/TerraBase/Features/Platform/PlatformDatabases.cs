using TerraBase.Errors;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Platform;

/// <summary>
/// TerraScale's own databases (dogfooding): one organization and a separate project and database for ts-auth and for the
/// dashboard, under fixed ids so every node and region that seeds them writes the same records. Project tokens trust only
/// the global ts-auth issuer. Access requires the
/// platform operator on the internal listener, a matching scoped project token, or a platform admin through explore.
/// </summary>
public static class PlatformDatabases
{
    /// <summary>The platform organization, on the enterprise plan (no per-tenant request budget).</summary>
    public const string OrganizationId = "00000000000000000000000001";

    /// <summary>The ts-auth project; ts-auth names it as its storage project (App.StorageProject).</summary>
    public const string AuthProjectId = "00000000000000000000000002";

    /// <summary>The dashboard project, distinct from ts-auth's storage project.</summary>
    public const string DashboardProjectId = "00000000000000000000000005";

    /// <summary>ts-auth's fixed database id.</summary>
    public const string AuthId = "00000000000000000000000003";

    /// <summary>The dashboard's fixed database id.</summary>
    public const string DashboardId = "00000000000000000000000004";

    /// <summary>ts-auth's database. ts-auth creates its collections itself at startup.</summary>
    public const string Auth = "ts-auth";

    /// <summary>The dashboard's database. The dashboard creates its collections itself.</summary>
    public const string Dashboard = "ts-dashboard";

    /// <summary>Every platform database, name to fixed database and owning project ids.</summary>
    public static IReadOnlyDictionary<string, (string Id, string ProjectId)> Databases { get; } = new Dictionary<string, (string Id, string ProjectId)>(StringComparer.Ordinal)
    {
        [Auth] = (AuthId, AuthProjectId),
        [Dashboard] = (DashboardId, DashboardProjectId),
    };

    /// <summary>Whether <paramref name="organizationId"/> is the platform organization.</summary>
    public static bool IsPlatform(string? organizationId) => organizationId == OrganizationId;

    /// <summary>The placement a seeding node writes: every region, including regions that join later, managed from <paramref name="region"/>.</summary>
    public static DatabasePlacement Placement(string region) => new(region, [], 1, AllRegions: true);

    /// <summary>Registers the seed.</summary>
    public static IServiceCollection AddTerraBasePlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<PlatformSeed>();
        return services;
    }
}

/// <summary>The platform organization, projects, bindings and databases exist as <see cref="PlatformDatabases"/> records them, or why not yet.</summary>
public readonly union EnsurePlatformResult(Done, Conflict, Unavailable);

/// <summary>Creates the platform records idempotently in this node's catalog.</summary>
public interface IPlatformCatalog
{
    /// <summary>
    /// Creates whatever is missing of the platform organization, project and databases, and checks what exists matches.
    /// Safe to run concurrently and repeatedly: every write is conditional on absence, under the fixed ids.
    /// <see cref="Conflict"/> when a fixed id or name holds something else (never overwritten).
    /// </summary>
    ValueTask<EnsurePlatformResult> EnsurePlatformAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Seeds the platform databases on every start, in the background so TerraBase serves at once: retries until the catalog
/// takes the writes.
/// </summary>
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
                        // Never overwritten; an operator must look. Not retried: nothing changes by itself.
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
