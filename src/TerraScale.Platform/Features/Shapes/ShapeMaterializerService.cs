using System.Collections.Concurrent;
using System.Threading.Channels;
using ZLogger;

namespace TerraScale.Platform.Features.Shapes;

public sealed class ShapeMaterializerService(
    ShapeRegistry registry,
    ShapeSealer sealer,
    ShapeNotifier notifier,
    TerraBaseClient terraBase,
    TerraBaseQuic quic,
    PlatformOptions options,
    PlatformReadiness readiness,
    PlatformMetrics metrics,
    TimeProvider timeProvider,
    ILogger<ShapeMaterializerService> logger) : BackgroundService
{
    private const int ClaimLimit = 64;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Reconnect = TimeSpan.FromSeconds(1);
    private readonly Channel<Ulid> _dirty = Channel.CreateBounded<Ulid>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<Ulid, string> _pending = new();
    private readonly Dictionary<Ulid, Feed> _feeds = [];
    private readonly string _owner = options.Region + "/" + options.Node + "/" + Ulid.NewUlid();
    private IReadOnlyList<ShapeRecord> _owned = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Shapes is null)
        {
            logger.ZLogInformation($"Shape materialization is off: TERRABASE_SHAPES_* is not configured");
            return;
        }

        try
        {
            await Task.WhenAll(ManageAsync(stoppingToken), MaterializeAsync(stoppingToken));
        }
        finally
        {
            foreach (var feed in _feeds.Values)
            {
                await feed.StopAsync();
            }
        }
    }

    private async Task ManageAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!readiness.SchemaReady)
            {
                continue;
            }

            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Shape lease pass failed");
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var owned = await registry.ClaimAsync(_owner, Lease, ClaimLimit, cancellationToken) switch
        {
            IReadOnlyList<ShapeRecord> claimed => claimed,
            Unavailable unavailable => Deferred<ShapeRecord>(unavailable.Message),
        };
        Volatile.Write(ref _owned, owned);
        var active = await registry.ActiveDatabasesAsync(cancellationToken) switch
        {
            IReadOnlyList<Ulid> databases => databases,
            Unavailable unavailable => Deferred<Ulid>(unavailable.Message),
        };
        var wanted = active.ToHashSet();
        foreach (var database in _feeds.Keys.Where(database => !wanted.Contains(database)).ToArray())
        {
            await _feeds[database].StopAsync();
            _ = _feeds.Remove(database);
        }

        foreach (var database in wanted.Where(database => !_feeds.ContainsKey(database)))
        {
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _feeds[database] = new Feed(stop, FollowAsync(database, stop.Token));
        }

        foreach (var shape in owned)
        {
            await EnqueueAsync(shape.Database, "", cancellationToken);
        }
    }

    private async Task FollowAsync(Ulid database, CancellationToken cancellationToken)
    {
        var after = "";
        while (!cancellationToken.IsCancellationRequested)
        {
            await EnqueueAsync(database, after, cancellationToken);
            after = quic.IsEnabled
                ? await FollowQuicAsync(database, after, cancellationToken)
                : await FollowHttpAsync(database, after, cancellationToken);
            await Task.WhenAny(Task.Delay(Reconnect, timeProvider, cancellationToken));
        }
    }

    private async Task<string> FollowQuicAsync(Ulid database, string after, CancellationToken cancellationToken)
    {
        var position = after;
        await foreach (var item in quic.SubscribeAsync(database, after, cancellationToken).WithCancellation(cancellationToken))
        {
            var bookmark = item switch
            {
                ChangefeedHttpItem entry => Seen("quic", entry.Bookmark),
                Unavailable unavailable => Broken(database, unavailable),
            };
            if (bookmark is null)
            {
                return position;
            }

            position = bookmark.Length > 0 ? bookmark : position;
            notifier.Signal(database);
            await EnqueueAsync(database, position, cancellationToken);
        }

        return position;
    }

    private async Task<string> FollowHttpAsync(Ulid database, string after, CancellationToken cancellationToken)
    {
        var position = after;
        await foreach (var item in terraBase.ChangefeedAsync(database, after, cancellationToken).WithCancellation(cancellationToken))
        {
            var bookmark = item switch
            {
                ChangefeedHttpItem entry => entry.Bookmark,
                Unavailable unavailable => Broken(database, unavailable),
            };
            if (bookmark is null)
            {
                return position;
            }

            position = bookmark.Length > 0 ? bookmark : position;
            notifier.Signal(database);
            await EnqueueAsync(database, position, cancellationToken);
        }

        return position;
    }

    private async Task MaterializeAsync(CancellationToken stoppingToken)
    {
        await foreach (var database in _dirty.Reader.ReadAllAsync(stoppingToken))
        {
            if (!_pending.TryRemove(database, out var bookmark))
            {
                continue;
            }

            foreach (var shape in Volatile.Read(ref _owned).Where(shape => shape.Database == database))
            {
                try
                {
                    await RefreshAsync(shape, bookmark, stoppingToken);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.ZLogWarning(error, $"Shape {shape.Handle} materialization failed");
                }
            }
        }
    }

    private async Task RefreshAsync(ShapeRecord shape, string bookmark, CancellationToken cancellationToken)
    {
        var query = new ShapeQuery(shape.Table, shape.Where, shape.Columns);
        var outcome = await terraBase.ExecuteLowLatencyAsync(shape.Database, SqlValues.Batch(query.Select()), SqlContext.After(bookmark), cancellationToken) switch
        {
            SqlExecuteResponse response => ShapeQuery.Snapshot(response.Results[0]) switch
            {
                ShapeSnapshot snapshot => await SealAsync(shape, snapshot, cancellationToken),
                Unavailable unavailable => unavailable.Message,
            },
            SqlFailure failure => failure.Error.Message,
            NotFound missing => missing.Message ?? "the database no longer exists",
            Unavailable unavailable => unavailable.Message,
        };
        if (outcome is not null)
        {
            logger.ZLogWarning($"Shape {shape.Handle} was not materialized: {outcome}");
        }
    }

    private async Task<string?> SealAsync(ShapeRecord shape, ShapeSnapshot snapshot, CancellationToken cancellationToken) =>
        await registry.ReadAsync(shape.Handle, cancellationToken) switch
        {
            ShapeRecord current => await sealer.SyncAsync(current, snapshot, cancellationToken) switch
            {
                ShapeRecord => null,
                Unavailable unavailable => unavailable.Message,
            },
            NotFound missing => missing.Message,
            Unavailable unavailable => unavailable.Message,
        };

    private async ValueTask EnqueueAsync(Ulid database, string bookmark, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_pending.TryAdd(database, bookmark))
            {
                await _dirty.Writer.WriteAsync(database, cancellationToken);
                return;
            }

            if (_pending.TryGetValue(database, out var existing) && _pending.TryUpdate(database, bookmark.Length > 0 ? bookmark : existing, existing))
            {
                return;
            }
        }
    }

    private string Seen(string transport, string bookmark)
    {
        metrics.ChangefeedItem(transport);
        return bookmark;
    }

    private string? Broken(Ulid database, Unavailable unavailable)
    {
        logger.ZLogWarning($"Changefeed for {database} broke: {unavailable.Message}");
        return null;
    }

    private T[] Deferred<T>(string message)
    {
        logger.ZLogWarning($"Shape coordination deferred: {message}");
        return [];
    }

    private sealed record Feed(CancellationTokenSource Stop, Task Running)
    {
        public async Task StopAsync()
        {
            await Stop.CancelAsync();
            await Task.WhenAny(Running);
            Stop.Dispose();
        }
    }
}
