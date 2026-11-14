using System.Diagnostics;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Features.HomePush;
using TerraBase.Features.Platform;
using TerraBase.Features.Replicas;
using ZLogger;
using HomePushService = TerraBase.Features.HomePush.HomePush;

namespace TerraBase.Features.Health;

public enum ReadinessCondition
{
    Stopping,
    CatalogClosed,
    PlatformSeedPending,
    RegionalWriterUnknown,
    HomePushStopped,
    HomeLeasesPending,
    StorageUnready,
}

public interface INodeReadiness
{
    ValueTask<ReadinessCondition?> UnreadyAsync(CancellationToken cancellationToken);
}

public static class ReadinessConditions
{
    public static string Label(ReadinessCondition condition) => condition switch
    {
        ReadinessCondition.Stopping => "stopping",
        ReadinessCondition.CatalogClosed => "catalog_closed",
        ReadinessCondition.PlatformSeedPending => "platform_seed_pending",
        ReadinessCondition.RegionalWriterUnknown => "regional_writer_unknown",
        ReadinessCondition.HomePushStopped => "home_push_stopped",
        ReadinessCondition.HomeLeasesPending => "home_leases_pending",
        ReadinessCondition.StorageUnready => "storage_unready",
        _ => throw new UnreachableException(),
    };
}

internal sealed class NodeReadiness(
    IStorageEngine engine, IHostApplicationLifetime lifetime, ICatalogGate gate, PlatformSeedProgress platform,
    RegionalOwners owners, TerraBaseOptions options,
    HomePushService push, HomeLeases leases, TimeProvider time, ILogger<NodeReadiness> logger) : INodeReadiness
{
    private static readonly TimeSpan ReportAfter = TimeSpan.FromSeconds(10);
    private readonly Lock _report = new();
    private long? _unreadySince;
    private ReadinessCondition? _reported;

    public async ValueTask<ReadinessCondition?> UnreadyAsync(CancellationToken cancellationToken)
    {
        var condition = await ConditionAsync(cancellationToken).ConfigureAwait(false);
        Report(condition);
        return condition;
    }

    private async ValueTask<ReadinessCondition?> ConditionAsync(CancellationToken cancellationToken)
    {
        var clustered = options is { NatsUrl: not null, PeersFile: not null };
        ReadinessCondition? blocked = lifetime.ApplicationStopping.IsCancellationRequested ? ReadinessCondition.Stopping
            : !gate.IsOpen ? ReadinessCondition.CatalogClosed
            : !platform.Ready ? ReadinessCondition.PlatformSeedPending
            : clustered && owners.Find(options.Region) is null ? ReadinessCondition.RegionalWriterUnknown
            : clustered && !push.Started ? ReadinessCondition.HomePushStopped
            : clustered && !leases.Ready ? ReadinessCondition.HomeLeasesPending
            : null;
        if (blocked is not null)
        {
            return blocked;
        }

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        try
        {
            return !await engine.IsReadyAsync(stopping.Token).ConfigureAwait(false) ? ReadinessCondition.StorageUnready
                : lifetime.ApplicationStopping.IsCancellationRequested ? ReadinessCondition.Stopping
                : null;
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return ReadinessCondition.Stopping;
        }
    }

    private void Report(ReadinessCondition? condition)
    {
        TimeSpan waited;
        ReadinessCondition? recovered = null;
        ReadinessCondition? held = null;
        lock (_report)
        {
            var now = time.GetTimestamp();
            waited = _unreadySince is { } since ? time.GetElapsedTime(since, now) : TimeSpan.Zero;
            if (condition is null)
            {
                recovered = _reported;
                _unreadySince = null;
                _reported = null;
            }
            else
            {
                _unreadySince ??= now;
                if (waited >= ReportAfter && _reported != condition)
                {
                    _reported = condition;
                    held = condition;
                }
            }
        }

        if (held is { } holding)
        {
            logger.ZLogWarning($"This node has not been ready for {(int)waited.TotalSeconds:@Seconds} s: {ReadinessConditions.Label(holding):@Condition} holds readiness");
        }
        else if (recovered is { } previous)
        {
            logger.ZLogInformation($"This node is ready after {(int)waited.TotalSeconds:@Seconds} s; readiness was last held by {ReadinessConditions.Label(previous):@Condition}");
        }
    }
}
public static class HealthExtensions
{
    public static IServiceCollection AddTerraBaseHealth(this IServiceCollection services)
    {
        services.AddSingleton<INodeReadiness, NodeReadiness>();
        return services;
    }
}
