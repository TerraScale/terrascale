using TerraBase.Cluster;
using TerraBase.Configuration;
using TerraBase.Continuity;
using TerraBase.Engine;
using TerraBase.Features.Health;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Storage;
using ZLogger;

namespace TerraBase.Hosting;

internal sealed class SystemBootstrap(
    SystemDatabase system,
    PlatformCatalog platform,
    ManagementService management,
    ClusterOptions cluster,
    StorageEngineOptions storage,
    AntiEntropyService antiEntropy,
    IContinuityDatabaseCatalog catalog,
    NodeReadiness readiness,
    IHostApplicationLifetime lifetime,
    ILogger<SystemBootstrap> logger)
    : IHostedService
{
    private static readonly TimeSpan StartupSyncBudget = TimeSpan.FromSeconds(5);

    public ConfigurationError? Failure { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Func<CancellationToken, ValueTask>? sync = cluster.Peers.Count > 0 ? SyncSystemAsync : null;
        var opened = await system.OpenAsync(cluster.IsBootstrapNode, sync, cancellationToken) switch
        {
            Done when cluster.IsBootstrapNode => await platform.EnsureAsync(cancellationToken),
            Done done => done,
            ConfigurationError error => error,
        };
        Failure = opened switch
        {
            Done => null,
            ConfigurationError error => error,
        };
        if (Failure is { } failure)
        {
            logger.ZLogCritical($"TerraBase could not open its system catalog: {failure.Message}");
            lifetime.StopApplication();
            return;
        }

        if (cluster.Peers.Count > 0)
        {
            await SyncOnStartupAsync(cancellationToken);
        }

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
            List<Ulid> local = [];
            foreach (var id in active)
            {
                if (id == SystemDatabase.Id)
                {
                    continue;
                }

                if (File.Exists(Path.Combine(storage.RootDirectory, id.ToString(), string.Concat(id.ToString(), ".db"))))
                {
                    local.Add(id);
                }
            }

            await Parallel.ForEachAsync(
                local,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token },
                async (id, ct) => _ = await antiEntropy.SyncAsync(id, ct));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}
