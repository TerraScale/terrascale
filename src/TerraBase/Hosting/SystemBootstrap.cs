using TerraBase.Cluster;
using TerraBase.Configuration;
using TerraBase.Continuity;
using TerraBase.Engine;
using TerraBase.Features.Health;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Features.UserAuth;
using TerraBase.Storage;
using ZLogger;

namespace TerraBase.Hosting;

internal sealed class SystemBootstrap(
    SystemDatabase system,
    PlatformCatalog platform,
    ManagementService management,
    TerraBaseOptions options,
    ClusterOptions cluster,
    PeerHealthMonitor health,
    StorageEngineOptions storage,
    AntiEntropyService antiEntropy,
    IContinuityDatabaseCatalog catalog,
    TokenVerifier tokens,
    NodeReadiness readiness,
    IHostApplicationLifetime lifetime,
    ILogger<SystemBootstrap> logger)
    : IHostedService
{
    private const int WarmStartupTenants = 8;
    private static readonly TimeSpan StartupSyncBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AuthWarmupBudget = TimeSpan.FromSeconds(3);

    public ConfigurationError? Failure { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (SqliteStorageEngine.RecoverBranchStaging(storage.RootDirectory) is { } stagingFailure)
        {
            Failure = new ConfigurationError(stagingFailure.Message);
            logger.ZLogCritical($"TerraBase could not clean interrupted database branches: {stagingFailure.Message}");
            lifetime.StopApplication();
            return;
        }

        Func<CancellationToken, ValueTask>? sync = cluster.Peers.Count > 0 ? SyncSystemAsync : null;
        bool AnyRemotePeerReachable() => cluster.RemotePeers.Any(peer => health.IsReachable(peer.NodeId));
        var openedOutcome = await system.OpenAsync(
            cluster.IsBootstrapNode,
            cluster.IsRegionBootstrapNode,
            options.BootstrapImmediate,
            AnyRemotePeerReachable,
            sync,
            cancellationToken);
        var singleRegionRollout = cluster.RemotePeers.Count > 0 && (options.BootstrapImmediate || !AnyRemotePeerReachable());
        var shouldSeedPlatform = cluster.IsBootstrapNode || (cluster.IsRegionBootstrapNode && singleRegionRollout);
        var opened = openedOutcome switch
        {
            Completed when shouldSeedPlatform => await platform.EnsureAsync(cancellationToken, allowFastFallback: singleRegionRollout),
            Completed done => done,
            ConfigurationError error => error,
        };
        Failure = opened switch
        {
            Completed => null,
            ConfigurationError error => error,
        };
        if (Failure is { } failure)
        {
            logger.ZLogCritical($"TerraBase could not open its system catalog: {failure.Message}");
            lifetime.StopApplication();
            return;
        }

        await (cluster.Peers.Count > 0
            ? Task.WhenAll(SyncOnStartupAsync(cancellationToken), tokens.WarmPlatformAsync(AuthWarmupBudget, cancellationToken))
            : tokens.WarmPlatformAsync(AuthWarmupBudget, cancellationToken));

        var (node, region, bootstrap) = (cluster.NodeId, cluster.Region, cluster.IsBootstrapNode);
        logger.ZLogInformation($"System catalog open on {node}.{region} (bootstrap node: {bootstrap})");
        var shredded = await management.SweepDeletedAsync(cancellationToken);
        if (shredded > 0)
        {
            logger.ZLogInformation($"Finished {shredded:@Shredded} interrupted database deletions");
        }

        lifetime.ApplicationStarted.Register(readiness.Ready);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async ValueTask SyncSystemAsync(CancellationToken cancellationToken) =>
        _ = await antiEntropy.SyncAsync(SystemDatabase.Id, cancellationToken);

    private async Task SyncOnStartupAsync(CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(StartupSyncBudget);
        try
        {
            _ = await antiEntropy.SyncAsync(SystemDatabase.Id, budget.Token);
            var active = await catalog.ListActiveDatabasesAsync(budget.Token);
            List<(Ulid Id, long LastWriteTicks)> warm = new(WarmStartupTenants + 1);
            List<Ulid> cold = [];
            foreach (var id in active)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (id == SystemDatabase.Id)
                {
                    continue;
                }

                var dbPath = Path.Combine(storage.RootDirectory, id.ToString(), string.Concat(id.ToString(), ".db"));
                if (!File.Exists(dbPath))
                {
                    continue;
                }

                var lastWrite = Math.Max(File.GetLastWriteTimeUtc(dbPath).Ticks, File.GetLastWriteTimeUtc(string.Concat(dbPath, "-wal")).Ticks);
                var candidate = (Id: id, LastWriteTicks: lastWrite);
                if (warm.Count < WarmStartupTenants || CompareStartupOrder(candidate, warm[^1]) < 0)
                {
                    InsertWarm(warm, candidate, cold);
                }
                else
                {
                    cold.Add(id);
                }
            }

            await Parallel.ForEachAsync(
                warm,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token },
                async (entry, token) => _ = await antiEntropy.SyncAsync(entry.Id, token));

            await Parallel.ForEachAsync(
                cold,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token },
                async (id, token) => _ = await antiEntropy.SyncTransientAsync(id, token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static void InsertWarm(List<(Ulid Id, long LastWriteTicks)> warm, (Ulid Id, long LastWriteTicks) candidate, List<Ulid> cold)
    {
        var index = warm.Count;
        while (index > 0 && CompareStartupOrder(candidate, warm[index - 1]) < 0)
        {
            index--;
        }

        warm.Insert(index, candidate);
        if (warm.Count > WarmStartupTenants)
        {
            cold.Add(warm[^1].Id);
            warm.RemoveAt(warm.Count - 1);
        }
    }

    private static int CompareStartupOrder((Ulid Id, long LastWriteTicks) left, (Ulid Id, long LastWriteTicks) right)
    {
        var byTime = right.LastWriteTicks.CompareTo(left.LastWriteTicks);
        return byTime != 0 ? byTime : left.Id.CompareTo(right.Id);
    }
}
