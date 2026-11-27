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
    private const int AuditPage = 256;
    private const int AuditRetirements = 16;
    private const string RefusedFilter = "its persisted filter is no longer accepted";
    private const string IdleShape = "it was not read for a day";
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleTick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Reconnect = TimeSpan.FromSeconds(1);
    private readonly Channel<Ulid> _dirty = Channel.CreateBounded<Ulid>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<Ulid, string> _pending = new();
    private readonly Dictionary<Ulid, ChangeSubscription> _subscriptions = [];
    private readonly string _owner = options.Region + "/" + options.Node + "/" + Ulid.NewUlid();
    private IReadOnlyList<ShapeRecord> _owned = [];
    private string _audited = ShapeQuery.NewHandle();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Shapes is null)
        {
            logger.ZLogInformation($"Shape materialization is off: TERRABASE_SHAPES_* is not configured");
            return;
        }

        try
        {
            await Task.WhenAll(ManageAsync(stoppingToken), MaterializeAsync(stoppingToken), CleanAsync(stoppingToken));
        }
        finally
        {
            foreach (var subscription in _subscriptions.Values)
            {
                await subscription.StopAsync();
            }
        }
    }

    private async Task CleanAsync(CancellationToken stoppingToken)
    {
        var wait = Tick;
        while (true)
        {
            await Task.Delay(wait, timeProvider, stoppingToken);
            if (!readiness.SchemaReady)
            {
                wait = Tick;
                continue;
            }

            var cleaned = true;
            try
            {
                var drained = await sealer.DrainAsync(stoppingToken);
                if (drained.Deferred is { } undrained)
                {
                    logger.ZLogWarning($"Shape object cleanup deferred: {undrained}");
                }

                var audited = await AuditAsync(stoppingToken);
                cleaned = drained.Due > 0 || audited;
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Shape cleanup pass failed");
            }

            wait = cleaned ? Tick : IdleWake.Backoff(wait, IdleTick);
        }
    }

    private async Task ManageAsync(CancellationToken stoppingToken)
    {
        var backoff = Tick;
        var wait = Tick;
        var idle = false;
        while (true)
        {
            await (idle ? registry.Touched.WaitAsync(wait, timeProvider, stoppingToken) : Task.Delay(wait, timeProvider, stoppingToken));
            registry.Touched.Clear();
            if (!readiness.SchemaReady)
            {
                (backoff, wait, idle) = (Tick, Tick, false);
                continue;
            }

            var started = timeProvider.GetTimestamp();
            var following = true;
            try
            {
                following = await ReconcileAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Shape lease pass failed");
            }

            idle = !following;
            backoff = idle ? IdleWake.Backoff(backoff, IdleTick) : Tick;
            wait = idle ? backoff : Tick - TimeSpan.FromTicks(Math.Min(timeProvider.GetElapsedTime(started).Ticks, Tick.Ticks));
        }
    }

    private async Task<bool> ReconcileAsync(CancellationToken cancellationToken)
    {
        var ownedOutcome = await registry.ClaimAsync(_owner, Lease, ClaimLimit, cancellationToken);
        var owned = ownedOutcome switch
        {
            ClaimedShapes { Items: var claimed } => claimed,
            PlatformUnavailable unavailable => Deferred<ShapeRecord>(unavailable.Message),
        };
        Volatile.Write(ref _owned, owned);
        var activeOutcome = await registry.ActiveDatabasesAsync(cancellationToken);
        var active = activeOutcome switch
        {
            ActiveShapeDatabases { Items: var databases } => databases,
            PlatformUnavailable unavailable => Kept(unavailable.Message),
        };
        var wanted = active.ToHashSet();
        foreach (var database in _subscriptions.Keys.Where(database => !wanted.Contains(database)).ToArray())
        {
            await _subscriptions[database].StopAsync();
            _ = _subscriptions.Remove(database);
        }

        foreach (var database in wanted.Where(database => !_subscriptions.ContainsKey(database)))
        {
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _subscriptions[database] = new ChangeSubscription(stop, FollowAsync(database, stop.Token));
        }

        foreach (var shape in owned)
        {
            await EnqueueAsync(shape.Database, "", cancellationToken);
        }

        return owned.Count > 0 || _subscriptions.Count > 0;
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
                PlatformUnavailable unavailable => Broken(database, unavailable),
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
                PlatformUnavailable unavailable => Broken(database, unavailable),
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
                if (!Volatile.Read(ref _owned).Any(owned => owned.Handle == shape.Handle))
                {
                    continue;
                }

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
        var outcome = Accepted(shape)
            ? await SnapshotAsync(shape, bookmark, cancellationToken)
            : Retire(shape.Handle, RefusedFilter, await registry.RetireShapeAsync(shape.Handle, cancellationToken));
        if (outcome is not null)
        {
            logger.ZLogWarning($"Shape {shape.Handle} was not materialized: {outcome}");
        }
    }

    private static bool Accepted(ShapeRecord shape) =>
        ShapeQuery.IsTable(shape.Table) && ShapeQuery.IsSafeFilter(shape.Where) && (shape.Columns is null || ShapeQuery.ColumnList(shape.Columns) is not null);

    private async Task<string?> SnapshotAsync(ShapeRecord shape, string bookmark, CancellationToken cancellationToken)
    {
        var query = new ShapeQuery(shape.Table, shape.Where, shape.Columns);
        var outcomeOutcome = await terraBase.ExecuteLowLatencyAsync(shape.Database, SqlValues.Batch(query.Select()), SqlContext.After(bookmark), cancellationToken);
        return outcomeOutcome switch
        {
            SqlExecuteResponse response => ShapeQuery.Snapshot(response.Results[0]) switch
            {
                ShapeSnapshot snapshot => await SealAsync(shape, snapshot, cancellationToken),
                PlatformUnavailable unavailable => unavailable.Message,
            },
            SqlFailure { Error.SqlState: "42P01" or "42703" } failure =>
                Retire(shape.Handle, failure.Error.Message, await registry.RetireShapeAsync(shape.Handle, cancellationToken)),
            SqlFailure failure => failure.Error.Message,
            PlatformNotFound missing => missing.Message ?? "the database no longer exists",
            PlatformUnavailable unavailable => unavailable.Message,
        };
    }

    private string? Retire(string subject, string reason, RetireShapeOutcome outcome) =>
        outcome switch
        {
            ShapeRetirement retired => Retired(subject, reason, retired),
            PlatformUnavailable unavailable => $"{reason}; retiring the shape failed: {unavailable.Message}",
        };

    private string? Retired(string subject, string reason, ShapeRetirement retired)
    {
        if (retired.Handles.Count == 0)
        {
            return null;
        }

        sealer.ForgetShapes(retired.Handles);
        var handles = retired.Handles.ToHashSet(StringComparer.Ordinal);
        var owned = Volatile.Read(ref _owned);
        while (Interlocked.CompareExchange(ref _owned, [.. owned.Where(shape => !handles.Contains(shape.Handle))], owned) is var seen && !ReferenceEquals(seen, owned))
        {
            owned = seen;
        }

        logger.ZLogInformation($"Retired {retired.Handles.Count} shapes for {subject}: {reason}");
        return null;
    }

    private async Task<bool> AuditAsync(CancellationToken cancellationToken)
    {
        var pageOutcome = await registry.PageAsync(_audited, AuditPage, cancellationToken);
        var page = pageOutcome switch
        {
            ShapePage { Items: var items } => items,
            PlatformUnavailable unavailable => Deferred<AuditedShape>(unavailable.Message),
        };
        AuditedShape[] due = [.. page.Where(static audited => audited.Idle || !Accepted(audited.Shape)).Take(AuditRetirements)];
        _audited = (due.Length, page.Count) switch
        {
            (AuditRetirements, _) => due[^1].Shape.Handle,
            (_, < AuditPage) => "",
            _ => page[^1].Shape.Handle,
        };
        foreach (var (shape, _) in due)
        {
            var outcome = Accepted(shape)
                ? Retire(shape.Handle, IdleShape, await registry.RetireIdleShapeAsync(shape.Handle, cancellationToken))
                : Retire(shape.Handle, RefusedFilter, await registry.RetireShapeAsync(shape.Handle, cancellationToken));
            if (outcome is not null)
            {
                logger.ZLogWarning($"Shape {shape.Handle} was not retired: {outcome}");
            }
        }

        return due.Length > 0 || page.Count == AuditPage;
    }

    private async Task<string?> SealAsync(ShapeRecord shape, ShapeSnapshot snapshot, CancellationToken cancellationToken)
    {
        var outcome = await registry.ReadAsync(shape.Handle, cancellationToken);
        return outcome switch
        {
            ShapeRecord current => await SyncAsync(current),
            PlatformNotFound missing => missing.Message,
            PlatformUnavailable unavailable => unavailable.Message,
        };

        async ValueTask<string?> SyncAsync(ShapeRecord current)
        {
            var outcome = await sealer.SyncAsync(current, snapshot, cancellationToken);
            return outcome switch
            {
                ShapeRecord => null,
                PlatformUnavailable unavailable => unavailable.Message,
            };
        }
    }

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

    private string? Broken(Ulid database, PlatformUnavailable unavailable)
    {
        logger.ZLogWarning($"Changefeed for {database} broke: {unavailable.Message}");
        return null;
    }

    private IReadOnlyList<Ulid> Kept(string message)
    {
        logger.ZLogWarning($"Shape changefeed subscriptions kept unchanged: {message}");
        return [.. _subscriptions.Keys];
    }

    private T[] Deferred<T>(string message)
    {
        logger.ZLogWarning($"Shape coordination deferred: {message}");
        return [];
    }

    private sealed record ChangeSubscription(CancellationTokenSource Stop, Task Running)
    {
        public async Task StopAsync()
        {
            await Stop.CancelAsync();
            await Task.WhenAny(Running);
            Stop.Dispose();
        }
    }
}
