using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TerraBase.Continuity;

public sealed class ChangefeedHub(ContinuitySessions sessions, ContinuityOptions options, ContinuityMetrics metrics, ILogger<ChangefeedHub> logger) : IChangefeedHub
{
    private const int ReplayPageSize = 256;

    private readonly Lock _gate = new();
    private readonly Dictionary<Ulid, Channel<ChangefeedEntry>[]> _subscribers = [];

    public ValueTask PublishAsync(ChangefeedEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Channel<ChangefeedEntry>[] targets;
        lock (_gate)
        {
            targets = _subscribers.GetValueOrDefault(entry.DatabaseId, []);
        }

        foreach (var target in targets)
        {
            if (!target.Writer.TryWrite(entry))
            {
                Detach(entry.DatabaseId, target);
                target.Writer.TryComplete();
            }
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<ChangefeedEntry> SubscribeAsync(ChangefeedSubscription subscription, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var channel = Channel.CreateBounded<ChangefeedEntry>(new BoundedChannelOptions(options.ChangefeedSubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        Attach(subscription.DatabaseId, channel);
        var registration = cancellationToken.Register(() => channel.Writer.TryComplete());
        try
        {
            var cursor = new ChangefeedCursor(subscription.After);
            await foreach (var entry in ReplayAsync(subscription, cursor, cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                if (cursor.Accept(entry))
                {
                    metrics.Delivered();
                    yield return entry;
                }
            }

            while (!cancellationToken.IsCancellationRequested && await channel.Reader.WaitToReadAsync(CancellationToken.None))
            {
                while (!cancellationToken.IsCancellationRequested && channel.Reader.TryRead(out var entry))
                {
                    if (cursor.Accept(entry))
                    {
                        metrics.Delivered();
                        yield return entry;
                    }
                }
            }
        }
        finally
        {
            await registration.DisposeAsync();
            Detach(subscription.DatabaseId, channel);
            channel.Writer.TryComplete();
        }
    }

    private async IAsyncEnumerable<ChangefeedEntry> ReplayAsync(ChangefeedSubscription subscription, ChangefeedCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var opened = await sessions.OpenAsync(subscription.DatabaseId, cancellationToken);
        var sessionOrStop = opened switch
        {
            ISqlDatabaseSession available => available,
            ContinuitySessionMissing or ContinuitySessionUnavailable => null,
        };
        if (sessionOrStop is not { } session)
        {
            yield break;
        }

        await using (session)
        {
            var upTo = session.CurrentBookmark;
            foreach (var (origin, last) in upTo.Entries)
            {
                var next = subscription.After.Get(origin) + 1;
                while (next <= last && !cancellationToken.IsCancellationRequested)
                {
                    var read = await session.ReadCommitLogAsync(origin, next, ReplayPageSize, cancellationToken);
                    var page = read switch
                    {
                        CommitLogPage log => log,
                        CommitLogGap gap => Skip(subscription.DatabaseId, origin, gap),
                        StorageBusy busy => Stop(subscription.DatabaseId, origin, busy.Message),
                    };
                    if (page.Entries.Count == 0)
                    {
                        next = page.NextSequence > next ? page.NextSequence : last + 1;
                        continue;
                    }

                    foreach (var commit in page.Entries)
                    {
                        if (commit.Sequence > last)
                        {
                            break;
                        }

                        yield return new ChangefeedEntry(
                            subscription.DatabaseId,
                            commit.TxId,
                            commit.OriginRegion,
                            commit.Sequence,
                            commit.SchemaEpoch,
                            commit.Mode,
                            commit.DeltaPayload,
                            DateTimeOffset.FromUnixTimeMilliseconds(commit.Hlc.WallMs));
                    }

                    next = page.NextSequence;
                }

                cursor.Replayed(origin, last);
            }
        }
    }

    private CommitLogPage Skip(Ulid databaseId, string origin, CommitLogGap gap)
    {
        ContinuityLog.ReplayStopped(logger, databaseId, origin, "history before the oldest retained entry is no longer available; resuming from it");
        return new CommitLogPage(databaseId, origin, [], gap.OldestAvailable);
    }

    private CommitLogPage Stop(Ulid databaseId, string origin, string reason)
    {
        ContinuityLog.ReplayStopped(logger, databaseId, origin, reason);
        return new CommitLogPage(databaseId, origin, [], long.MaxValue);
    }

    private void Attach(Ulid databaseId, Channel<ChangefeedEntry> channel)
    {
        lock (_gate)
        {
            _subscribers[databaseId] = [.. _subscribers.GetValueOrDefault(databaseId, []), channel];
        }

        metrics.SubscriberOpened();
    }

    private void Detach(Ulid databaseId, Channel<ChangefeedEntry> channel)
    {
        var removed = false;
        lock (_gate)
        {
            if (_subscribers.TryGetValue(databaseId, out var existing))
            {
                var remaining = existing.Where(candidate => !ReferenceEquals(candidate, channel)).ToArray();
                removed = remaining.Length != existing.Length;
                if (remaining.Length == 0)
                {
                    _ = _subscribers.Remove(databaseId);
                }
                else
                {
                    _subscribers[databaseId] = remaining;
                }
            }
        }

        if (removed)
        {
            metrics.SubscriberClosed();
        }
    }
}

internal sealed class ChangefeedCursor
{
    private const int RecentCapacity = 4096;

    private readonly Dictionary<string, long> _floor = new(StringComparer.Ordinal);
    private readonly HashSet<Ulid> _recent = [];
    private readonly Queue<Ulid> _order = new();

    public ChangefeedCursor(CausalBookmark after)
    {
        foreach (var (origin, sequence) in after.Entries)
        {
            _floor[origin] = sequence;
        }
    }

    public void Replayed(string origin, long through)
    {
        if (through > _floor.GetValueOrDefault(origin))
        {
            _floor[origin] = through;
        }
    }

    public bool Accept(ChangefeedEntry entry)
    {
        if (entry.Sequence <= _floor.GetValueOrDefault(entry.OriginRegion) || !_recent.Add(entry.TxId))
        {
            return false;
        }

        _order.Enqueue(entry.TxId);
        if (_order.Count > RecentCapacity)
        {
            _ = _recent.Remove(_order.Dequeue());
        }

        return true;
    }
}
