using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Cluster;

public enum DatabaseResolveMode
{
    Warm,
    Transient,
}

public readonly union DatabaseResolveOutcome(ISqlDatabaseSession, DatabaseNotFound, ClusterUnavailable);

public delegate ValueTask<DatabaseResolveOutcome> DatabaseSessionResolver(Ulid databaseId, DatabaseResolveMode mode, CancellationToken cancellationToken);

public sealed class ClusterDatabases(DatabaseSessionResolver resolver, TimeProvider timeProvider)
{
    private const int CatchUpQueueCapacity = 1024;
    private const int PeriodicBatchSize = 32;

    private readonly ConcurrentDictionary<Ulid, TrackedDatabase> _tracked = new();
    private readonly Channel<QueuedCatchUp> _catchUp = Channel.CreateBounded<QueuedCatchUp>(new BoundedChannelOptions(CatchUpQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    internal ChannelReader<QueuedCatchUp> CatchUpRequests => _catchUp.Reader;

    public void Track(Ulid databaseId)
    {
        if (!_tracked.ContainsKey(databaseId))
        {
            _ = _tracked.GetOrAdd(databaseId, static _ => new TrackedDatabase());
        }
    }

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

    internal TrackedDatabase? BeginSync(Ulid databaseId) =>
        _tracked.TryGetValue(databaseId, out var tracked) && tracked.BeginSync(timeProvider.GetUtcNow(), onDemand: false) ? tracked : null;

    internal TrackedDatabase? BeginSync(QueuedCatchUp request) =>
        IsCurrent(request.DatabaseId, request.Tracked) && request.Tracked.BeginSync(timeProvider.GetUtcNow(), onDemand: true) ? request.Tracked : null;

    public bool RequestCatchUp(Ulid databaseId) => RequestCatchUp(databaseId, null);

    public bool RequestCatchUp(Ulid databaseId, CausalBookmark? peerFrontier) =>
        _tracked.TryGetValue(databaseId, out var tracked) && Enqueue(databaseId, tracked, peerFrontier);

    internal void EndSync(Ulid databaseId, TrackedDatabase tracked, bool progressed, bool requeue)
    {
        if (tracked.EndSync(timeProvider.GetUtcNow(), progressed) && requeue && IsCurrent(databaseId, tracked))
        {
            _ = Enqueue(databaseId, tracked, null);
        }
    }

    public void OnPeerRecovered()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (_, tracked) in _tracked)
        {
            tracked.ResetBackoff(now);
        }
    }

    internal bool IsCurrent(Ulid databaseId, TrackedDatabase tracked) =>
        _tracked.TryGetValue(databaseId, out var current) && ReferenceEquals(current, tracked);

    private bool Enqueue(Ulid databaseId, TrackedDatabase tracked, CausalBookmark? peerFrontier)
    {
        if (!tracked.QueueSync(peerFrontier, timeProvider.GetUtcNow()))
        {
            return false;
        }

        if (_catchUp.Writer.TryWrite(new QueuedCatchUp(databaseId, tracked)))
        {
            return true;
        }

        tracked.DeferQueuedSync(timeProvider.GetUtcNow());
        return false;
    }

    public ValueTask<DatabaseResolveOutcome> ResolveAsync(Ulid databaseId, CancellationToken cancellationToken) =>
        ResolveAsync(databaseId, DatabaseResolveMode.Warm, cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<DatabaseResolveOutcome>))]
    public async ValueTask<DatabaseResolveOutcome> ResolveAsync(Ulid databaseId, DatabaseResolveMode mode, CancellationToken cancellationToken)
    {
        _tracked.TryGetValue(databaseId, out var tracked);
        var resolved = await resolver(databaseId, mode, cancellationToken);
        return resolved switch
        {
            ISqlDatabaseSession session => Observed(session),
            DatabaseNotFound notFound => Missing(notFound, tracked, mode),
            ClusterUnavailable unavailable => unavailable,
        };
    }

    private DatabaseResolveOutcome Observed(ISqlDatabaseSession session)
    {
        if (_tracked.TryGetValue(session.DatabaseId, out var current))
        {
            current.Observe(session.CurrentBookmark);
        }

        return session;
    }

    private DatabaseResolveOutcome Missing(DatabaseNotFound missing, TrackedDatabase? tracked, DatabaseResolveMode mode)
    {
        if (mode == DatabaseResolveMode.Warm && tracked is not null)
        {
            _ = _tracked.TryRemove(KeyValuePair.Create(missing.DatabaseId, tracked));
        }

        return missing;
    }

    internal readonly record struct QueuedCatchUp(Ulid DatabaseId, TrackedDatabase Tracked);

    internal sealed class TrackedDatabase
    {
        private const int MaxFrontierEntries = 64;
        private static readonly TimeSpan InitialInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MaxOnDemandBackoff = TimeSpan.FromSeconds(10);

        private readonly Lock _gate = new();
        private CausalBookmark _bookmark = CausalBookmark.Empty;
        private CausalBookmark _requestedFrontier = CausalBookmark.Empty;
        private CausalBookmark _attemptedFrontier = CausalBookmark.Empty;
        private CausalBookmark _exhaustedFrontier = CausalBookmark.Empty;
        private DateTimeOffset _nextSync = DateTimeOffset.MinValue;
        private DateTimeOffset _onDemandNotBefore;
        private DateTimeOffset _bypassBlockedUntil;
        private TimeSpan _interval = InitialInterval;
        private TimeSpan _onDemandBackoff = InitialInterval;
        private bool _resync;
        private bool _syncing;
        private bool _queued;
        private bool _deferred;
        private bool _onDemandAttempt;
        private bool _bypassGranted;
        private bool _frontierSaturated;

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
            if (bookmark.IsEmpty)
            {
                return;
            }

            var current = Volatile.Read(ref _bookmark);
            if (ReferenceEquals(current, bookmark) || current.Dominates(bookmark))
            {
                return;
            }

            lock (_gate)
            {
                if (_bookmark.Dominates(bookmark))
                {
                    return;
                }

                Volatile.Write(ref _bookmark, bookmark.Dominates(_bookmark) ? bookmark : _bookmark.Merge(bookmark));
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
                _onDemandAttempt = onDemand || _deferred || !_requestedFrontier.IsEmpty;
                _deferred = false;
                _bypassGranted = false;
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
                    ClearBackoff();
                }
                else
                {
                    _interval = Doubled(_interval, IdleInterval);
                    RecordExhausted(_attemptedFrontier);
                    if (_onDemandAttempt)
                    {
                        _onDemandNotBefore = _bypassGranted ? default : now + _onDemandBackoff;
                        _onDemandBackoff = Doubled(_onDemandBackoff, MaxOnDemandBackoff);
                    }
                }

                _attemptedFrontier = CausalBookmark.Empty;
                _onDemandAttempt = false;
                _bypassGranted = false;
                _nextSync = now + _interval;
                var resync = _resync;
                _resync = false;
                return resync;
            }
        }

        public bool QueueSync(CausalBookmark? peerFrontier, DateTimeOffset now)
        {
            lock (_gate)
            {
                if (peerFrontier is not null && !IsCovered(peerFrontier))
                {
                    RequestFrontier(peerFrontier, now);
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
                if (_syncing)
                {
                    _resync = true;
                    return;
                }

                _deferred = true;
                var due = now < _onDemandNotBefore ? _onDemandNotBefore : now;
                _nextSync = due < _nextSync ? due : _nextSync;
            }
        }

        public void ResetBackoff(DateTimeOffset now)
        {
            lock (_gate)
            {
                ClearBackoff();
                _attemptedFrontier = CausalBookmark.Empty;
                _onDemandAttempt = false;
                if (_syncing)
                {
                    _resync = true;
                    return;
                }

                if (_deferred || _interval > InitialInterval)
                {
                    _interval = InitialInterval;
                    _nextSync = now < _nextSync ? now : _nextSync;
                }
            }
        }

        private void ClearBackoff()
        {
            _onDemandBackoff = InitialInterval;
            _onDemandNotBefore = default;
            _bypassBlockedUntil = default;
            _exhaustedFrontier = CausalBookmark.Empty;
            _frontierSaturated = false;
        }

        private void RequestFrontier(CausalBookmark peerFrontier, DateTimeOffset now)
        {
            var requested = _requestedFrontier.Merge(peerFrontier);
            if (requested.Entries.Count > MaxFrontierEntries)
            {
                return;
            }

            _requestedFrontier = requested;
            if (now < _bypassBlockedUntil)
            {
                return;
            }

            _bypassBlockedUntil = now + _onDemandBackoff;
            _bypassGranted = true;
            _onDemandNotBefore = default;
        }

        private void RecordExhausted(CausalBookmark attempted)
        {
            var exhausted = _exhaustedFrontier.Merge(attempted);
            if (exhausted.Entries.Count > MaxFrontierEntries)
            {
                _frontierSaturated = true;
                return;
            }

            _exhaustedFrontier = exhausted;
        }

        private bool IsCovered(CausalBookmark frontier)
        {
            if (_frontierSaturated)
            {
                return true;
            }

            var entries = frontier.Entries;
            for (var index = 0; index < entries.Count; index++)
            {
                var (origin, sequence) = entries[index];
                var applied = _bookmark.Get(origin);
                if (_exhaustedFrontier.Get(origin) <= applied && applied < sequence)
                {
                    return false;
                }
            }

            return true;
        }

        private static TimeSpan Doubled(TimeSpan interval, TimeSpan cap) => TimeSpan.FromTicks(Math.Min(interval.Ticks * 2, cap.Ticks));
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
            commit.Hlc.WallMs > 0 ? commit.Hlc.WallTime : timeProvider.GetUtcNow());
        foreach (var hub in _hubs)
        {
            await hub.PublishAsync(entry, cancellationToken);
        }
    }

    public void ObserveBookmark(Ulid databaseId, CausalBookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        if (_hubs.Length == 0 || bookmark.IsEmpty)
        {
            return;
        }

        foreach (var hub in _hubs)
        {
            hub.ObserveBookmark(databaseId, bookmark);
        }
    }
}
