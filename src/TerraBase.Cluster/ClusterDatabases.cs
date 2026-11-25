using System.Collections.Concurrent;
using System.Threading.Channels;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Cluster;

public readonly union DatabaseResolveOutcome(ISqlDatabaseSession, DatabaseNotFound, ClusterUnavailable);

public delegate ValueTask<DatabaseResolveOutcome> DatabaseSessionResolver(Ulid databaseId, CancellationToken cancellationToken);

public sealed class ClusterDatabases(DatabaseSessionResolver resolver, TimeProvider timeProvider)
{
    private const int CatchUpQueueCapacity = 1024;
    private const int PeriodicBatchSize = 32;

    private readonly ConcurrentDictionary<Ulid, TrackedDatabase> _tracked = new();
    private readonly Channel<Ulid> _catchUp = Channel.CreateBounded<Ulid>(new BoundedChannelOptions(CatchUpQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    internal ChannelReader<Ulid> CatchUpRequests => _catchUp.Reader;

    public void Track(Ulid databaseId) => _ = _tracked.GetOrAdd(databaseId, static _ => new TrackedDatabase());

    public void Untrack(Ulid databaseId) => _ = _tracked.TryRemove(databaseId, out _);

    public CausalBookmark Current(Ulid databaseId) =>
        _tracked.TryGetValue(databaseId, out var tracked) ? tracked.Bookmark : CausalBookmark.Empty;

    public void Observe(Ulid databaseId, CausalBookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        if (_tracked.TryGetValue(databaseId, out var tracked))
        {
            tracked.Observe(bookmark);
        }
    }

    internal IReadOnlyList<Ulid> Due()
    {
        var now = timeProvider.GetUtcNow();
        return [.. _tracked.Select(static entry => (entry.Key, Due: entry.Value.NextSync))
            .Where(entry => entry.Due <= now)
            .OrderBy(static entry => entry.Due)
            .Take(PeriodicBatchSize)
            .Select(static entry => entry.Key)];
    }

    internal TrackedDatabase? BeginSync(Ulid databaseId, bool onDemand) =>
        _tracked.TryGetValue(databaseId, out var tracked) && tracked.BeginSync(timeProvider.GetUtcNow(), onDemand) ? tracked : null;

    public bool RequestCatchUp(Ulid databaseId) => RequestCatchUp(databaseId, null);

    public bool RequestCatchUp(Ulid databaseId, CausalBookmark? peerFrontier) =>
        _tracked.TryGetValue(databaseId, out var tracked) && Enqueue(databaseId, tracked, peerFrontier);

    internal void EndSync(Ulid databaseId, TrackedDatabase tracked, bool progressed, bool requeue)
    {
        if (tracked.EndSync(timeProvider.GetUtcNow(), progressed) && requeue
            && _tracked.TryGetValue(databaseId, out var current) && ReferenceEquals(current, tracked))
        {
            _ = Enqueue(databaseId, tracked, null);
        }
    }

    private bool Enqueue(Ulid databaseId, TrackedDatabase tracked, CausalBookmark? peerFrontier)
    {
        if (!tracked.QueueSync(peerFrontier))
        {
            return false;
        }

        if (_catchUp.Writer.TryWrite(databaseId))
        {
            return true;
        }

        tracked.DeferQueuedSync(timeProvider.GetUtcNow());
        return false;
    }

    public async ValueTask<DatabaseResolveOutcome> ResolveAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        _tracked.TryGetValue(databaseId, out var tracked);
        var resolved = await resolver(databaseId, cancellationToken);
        return resolved switch
        {
            ISqlDatabaseSession session => Observed(session),
            DatabaseNotFound notFound => Missing(notFound, tracked),
            ClusterUnavailable unavailable => unavailable,
        };
    }

    private DatabaseResolveOutcome Observed(ISqlDatabaseSession session)
    {
        Observe(session.DatabaseId, session.CurrentBookmark);
        return session;
    }

    private DatabaseResolveOutcome Missing(DatabaseNotFound missing, TrackedDatabase? tracked)
    {
        if (tracked is not null)
        {
            _ = _tracked.TryRemove(KeyValuePair.Create(missing.DatabaseId, tracked));
        }

        return missing;
    }

    internal sealed class TrackedDatabase
    {
        private static readonly TimeSpan InitialInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(5);

        private readonly Lock _gate = new();
        private CausalBookmark _bookmark = CausalBookmark.Empty;
        private CausalBookmark _requestedFrontier = CausalBookmark.Empty;
        private CausalBookmark _attemptedFrontier = CausalBookmark.Empty;
        private CausalBookmark _exhaustedFrontier = CausalBookmark.Empty;
        private DateTimeOffset _nextSync = DateTimeOffset.MinValue;
        private DateTimeOffset _onDemandNotBefore;
        private TimeSpan _interval = InitialInterval;
        private TimeSpan _onDemandBackoff = InitialInterval;
        private bool _resync;
        private bool _syncing;
        private bool _queued;
        private bool _deferred;
        private bool _onDemandAttempt;

        public CausalBookmark Bookmark => Volatile.Read(ref _bookmark);

        public DateTimeOffset NextSync
        {
            get
            {
                lock (_gate)
                {
                    return _syncing ? DateTimeOffset.MaxValue : _nextSync;
                }
            }
        }

        public void Observe(CausalBookmark bookmark)
        {
            lock (_gate)
            {
                Volatile.Write(ref _bookmark, _bookmark.Merge(bookmark));
            }
        }

        public bool BeginSync(DateTimeOffset now, bool onDemand)
        {
            lock (_gate)
            {
                if (onDemand)
                {
                    _queued = false;
                }

                if (_syncing)
                {
                    _resync |= onDemand;
                    return false;
                }

                if (onDemand && now < _onDemandNotBefore)
                {
                    _deferred = true;
                    _nextSync = _onDemandNotBefore < _nextSync ? _onDemandNotBefore : _nextSync;
                    return false;
                }

                if (!onDemand && _nextSync > now)
                {
                    return false;
                }

                _syncing = true;
                _onDemandAttempt = onDemand || _deferred;
                _deferred = false;
                _attemptedFrontier = _requestedFrontier;
                _requestedFrontier = CausalBookmark.Empty;
                return true;
            }
        }

        public bool EndSync(DateTimeOffset now, bool progressed)
        {
            lock (_gate)
            {
                _syncing = false;
                if (progressed)
                {
                    _interval = InitialInterval;
                    _onDemandBackoff = InitialInterval;
                    _onDemandNotBefore = default;
                    _exhaustedFrontier = CausalBookmark.Empty;
                }
                else
                {
                    _interval = Doubled(_interval);
                    _exhaustedFrontier = _exhaustedFrontier.Merge(_attemptedFrontier);
                    _onDemandNotBefore = _exhaustedFrontier.Dominates(_requestedFrontier) ? now + _onDemandBackoff : default;
                    _onDemandBackoff = _onDemandAttempt ? Doubled(_onDemandBackoff) : _onDemandBackoff;
                }

                _attemptedFrontier = CausalBookmark.Empty;
                _nextSync = now + _interval;
                var resync = _resync;
                _resync = false;
                return resync;
            }
        }

        public bool QueueSync(CausalBookmark? peerFrontier)
        {
            lock (_gate)
            {
                if (peerFrontier is not null && !_exhaustedFrontier.Dominates(peerFrontier))
                {
                    _requestedFrontier = _requestedFrontier.Merge(peerFrontier);
                    _onDemandNotBefore = default;
                }

                if (_queued)
                {
                    return false;
                }

                _queued = true;
                return true;
            }
        }

        public void DeferQueuedSync(DateTimeOffset now)
        {
            lock (_gate)
            {
                _queued = false;
                _nextSync = now;
            }
        }

        private static TimeSpan Doubled(TimeSpan interval) => TimeSpan.FromTicks(Math.Min(interval.Ticks * 2, IdleInterval.Ticks));
    }
}

public sealed class ChangefeedPublisher(IEnumerable<IChangefeedHub> hubs, TimeProvider timeProvider)
{
    private readonly IChangefeedHub[] _hubs = [.. hubs];

    public bool IsEnabled => _hubs.Length > 0;

    public async ValueTask PublishAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (_hubs.Length == 0 || commit.Sequence == 0)
        {
            return;
        }

        var entry = new ChangefeedEntry(
            databaseId,
            commit.TxId,
            commit.OriginRegion,
            commit.Sequence,
            commit.SchemaEpoch,
            commit.Mode,
            commit.DeltaPayload,
            commit.Hlc.WallMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(commit.Hlc.WallMs) : timeProvider.GetUtcNow());
        foreach (var hub in _hubs)
        {
            await hub.PublishAsync(entry, cancellationToken);
        }
    }
}
