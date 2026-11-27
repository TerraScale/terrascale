using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using TerraBase.Hosting;
using TerraBase.Storage;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Engine;

internal readonly union TenantOpenOutcome(ISqlDatabaseSession, ResourceNotFound, BackendUnavailable);

internal readonly union TenantCreateOutcome(ISqlDatabaseSession, StateConflict, BackendUnavailable);

internal readonly union TenantReadyOutcome(ISqlDatabaseSession, BackendUnavailable);

internal readonly union TenantBranchOutcome(ISqlDatabaseSession, ResourceNotFound, StateConflict, BackendUnavailable);

internal readonly union TenantDeleteOutcome(Completed, BackendUnavailable);

internal sealed class TenantDatabases(ISqlStorageEngine engine, DatabaseKeyring keyring, IClusterCoordinator cluster, TimeProvider time, ILogger<TenantDatabases> logger) : IAsyncDisposable
{
    private static readonly BackendUnavailable MissingKey = new("the database's master key is not configured on this node", "master_key_missing");

    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<Ulid, SemaphoreSlim> _opening = new();
    private readonly ConcurrentDictionary<Ulid, TrackedSession> _sessions = new();

    public string ActiveKeyId => keyring.ActiveKeyId;

    public bool TryGetOpen(Ulid databaseId, [NotNullWhen(true)] out ISqlDatabaseSession? session)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(databaseId, out var tracked) && tracked.Session.IsUsable)
            {
                tracked.Touch(time.GetTimestamp());
                session = tracked.Session;
                return true;
            }
        }

        session = null;
        return false;
    }

    public bool TryGetOpen(Ulid databaseId, string keyId, [NotNullWhen(true)] out ISqlDatabaseSession? session)
    {
        lock (_gate)
        {
            if (TryFindTracked(databaseId, keyId, out var tracked))
            {
                tracked.Touch(time.GetTimestamp());
                session = tracked.Session;
                return true;
            }
        }

        session = null;
        return false;
    }

    private bool TryFindTracked(Ulid databaseId, string keyId, [NotNullWhen(true)] out TrackedSession? tracked)
    {
        if (_sessions.TryGetValue(databaseId, out var found)
            && found.Session.IsUsable
            && string.Equals(found.KeyId, keyId, StringComparison.Ordinal))
        {
            tracked = found;
            return true;
        }

        tracked = null;
        return false;
    }

    public ValueTask<TenantOpenOutcome> OpenAsync(Ulid databaseId, string keyId, CancellationToken cancellationToken)
    {
        if (TryGetOpen(databaseId, keyId, out var cached))
        {
            return ValueTask.FromResult(Opened(cached));
        }

        return OpenSlowAsync(databaseId, keyId, cancellationToken);
    }

    private ValueTask<TenantOpenOutcome> OpenSlowAsync(Ulid databaseId, string keyId, CancellationToken cancellationToken) =>
        keyring.Derive(databaseId, keyId) switch
        {
            DatabaseKey key => OpenWithKeyAsync(databaseId, keyId, key, cancellationToken),
            MasterKeyMissing => ValueTask.FromResult<TenantOpenOutcome>(MissingKey),
        };

    public async ValueTask<TenantCreateOutcome> CreateAsync(Ulid databaseId, string keyId, bool strict, CancellationToken cancellationToken) =>
        keyring.Derive(databaseId, keyId) switch
        {
            DatabaseKey key => await CreateWithKeyAsync(databaseId, keyId, key, strict, cancellationToken),
            MasterKeyMissing => MissingKey,
        };

    public ValueTask<TenantReadyOutcome> OpenOrCreateAsync(Ulid databaseId, string keyId, bool strict, CancellationToken cancellationToken)
    {
        if (TryGetOpen(databaseId, keyId, out var cached))
        {
            return ValueTask.FromResult(Ready(cached));
        }

        return OpenOrCreateSlowAsync(databaseId, keyId, strict, cancellationToken);
    }

    private async ValueTask<TenantReadyOutcome> OpenOrCreateSlowAsync(Ulid databaseId, string keyId, bool strict, CancellationToken cancellationToken)
    {
        var outcome = await OpenSlowAsync(databaseId, keyId, cancellationToken);
        return outcome switch
        {
            ISqlDatabaseSession existing => Ready(existing),
            ResourceNotFound => keyring.Derive(databaseId, keyId) switch
            {
                DatabaseKey key => await CreateLocalAsync(databaseId, keyId, key, strict, cancellationToken),
                MasterKeyMissing => MissingKey,
            },
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<TenantBranchOutcome> BranchAsync(
        ISqlDatabaseSession source, Ulid branchId, string keyId, CausalBookmark at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return keyring.Derive(branchId, keyId) switch
        {
            DatabaseKey key => await BranchWithKeyAsync(source, branchId, keyId, key, at, cancellationToken),
            MasterKeyMissing => MissingKey,
        };
    }

    private async ValueTask<TenantBranchOutcome> BranchWithKeyAsync(
        ISqlDatabaseSession source, Ulid branchId, string keyId, DatabaseKey key, CausalBookmark at, CancellationToken cancellationToken)
    {
        var gate = Gate(branchId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var outcome = await engine.BranchAsync(new DatabaseBranchRequest(source.DatabaseId, branchId, key.RawKey, at), cancellationToken);
            return outcome switch
            {
                ISqlDatabaseSession session => Branched(Track(branchId, keyId, Audited(session))),
                DatabaseNotFound missing => new ResourceNotFound(missing.Message),
                DatabaseAlreadyExists exists => new StateConflict(exists.Message),
                InvalidDatabaseRequest error => new StateConflict(error.Message, "branch_point_invalid"),
                StorageBusy busy => new BackendUnavailable(busy.Message, "storage_busy"),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<TenantDeleteOutcome> DeleteAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        await EvictAsync(databaseId);
        var outcome = await engine.CryptoShredAsync(databaseId, cancellationToken);
        return outcome switch
        {
            DatabaseShredded or DatabaseNotFound => new Completed(),
            StorageBusy busy => new BackendUnavailable(busy.Message, "storage_busy"),
        };
    }

    public async ValueTask EvictAsync(Ulid databaseId)
    {
        ISqlDatabaseSession? session = null;
        lock (_gate)
        {
            if (_sessions.TryRemove(databaseId, out var removed))
            {
                session = removed.Session;
            }

            cluster.UntrackDatabase(databaseId);
        }

        if (session is not null)
        {
            await session.DisposeAsync();
        }
    }

    public int TrimIdle(TimeSpan readerIdleAfter, TimeSpan shrinkWriterAfter)
    {
        TrackedSession[] tracked;
        lock (_gate)
        {
            tracked = [.. _sessions.Values];
        }

        var trimmed = 0;
        foreach (var entry in tracked)
        {
            if (entry.Session.IsUsable)
            {
                var shrinkAfter = IdleFor(entry) >= shrinkWriterAfter && SqliteStorageEngine.IsIdle(entry.Session, shrinkWriterAfter)
                    ? shrinkWriterAfter
                    : (TimeSpan?)null;
                trimmed += SqliteStorageEngine.TrimIdleResources(entry.Session, readerIdleAfter, shrinkAfter);
            }
        }

        return trimmed;
    }

    public IReadOnlyList<Ulid> IdleDatabaseIds(TimeSpan idleFor)
    {
        lock (_gate)
        {
            return [.. _sessions
                .Where(entry => entry.Key != SystemDatabase.Id && IdleFor(entry.Value) >= idleFor)
                .OrderBy(static entry => entry.Value.LastAccessTimestamp)
                .Select(static entry => entry.Key)];
        }
    }

    public async ValueTask<bool> EvictIdleAsync(Ulid databaseId, TimeSpan idleFor)
    {
        if (databaseId == SystemDatabase.Id)
        {
            return false;
        }

        var gate = Gate(databaseId);
        if (!gate.Wait(0))
        {
            return false;
        }

        try
        {
            ISqlDatabaseSession? evicted = null;
            lock (_gate)
            {
                if (_sessions.TryGetValue(databaseId, out var tracked)
                    && IdleFor(tracked) >= idleFor
                    && SqliteStorageEngine.IsIdle(tracked.Session, idleFor)
                    && _sessions.TryRemove(databaseId, out var removed))
                {
                    evicted = removed.Session;
                    cluster.UntrackDatabase(databaseId);
                }
            }

            if (evicted is null)
            {
                return false;
            }

            await evicted.DisposeAsync();
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public IReadOnlyList<ISqlDatabaseSession> OpenSessions()
    {
        lock (_gate)
        {
            return [.. _sessions.Values.Select(static tracked => tracked.Session)];
        }
    }

    public async ValueTask DisposeAsync()
    {
        ISqlDatabaseSession[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions.Values.Select(static tracked => tracked.Session)];
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync();
        }
    }

    private static TenantOpenOutcome Opened(ISqlDatabaseSession session) => new(session);

    private static TenantReadyOutcome Ready(ISqlDatabaseSession session) => new(session);

    private static TenantBranchOutcome Branched(ISqlDatabaseSession session) => new(session);

    private static TenantCreateOutcome Created(ISqlDatabaseSession session) => new(session);

    private SemaphoreSlim Gate(Ulid databaseId) => _opening.GetOrAdd(databaseId, static _ => new SemaphoreSlim(1, 1));

    private async ValueTask<TenantCreateOutcome> CreateWithKeyAsync(Ulid databaseId, string keyId, DatabaseKey key, bool strict, CancellationToken cancellationToken)
    {
        var gate = Gate(databaseId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var outcome = await engine.CreateAsync(new DatabaseCreateRequest(databaseId, key.RawKey, strict), cancellationToken);
            return outcome switch
            {
                ISqlDatabaseSession session => Created(Track(databaseId, keyId, session)),
                DatabaseAlreadyExists exists => new StateConflict(exists.Message),
                InvalidDatabaseRequest error => new BackendUnavailable(error.Message, "database_key_invalid"),
                StorageBusy busy => new BackendUnavailable(busy.Message, "storage_busy"),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<TenantOpenOutcome> OpenWithKeyAsync(Ulid databaseId, string keyId, DatabaseKey key, CancellationToken cancellationToken)
    {
        var gate = Gate(databaseId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (await UsableAsync(databaseId, keyId) is { } raced)
            {
                return Opened(raced);
            }

            var outcome = await engine.OpenAsync(new DatabaseOpenRequest(databaseId, key.RawKey), cancellationToken);
            return outcome switch
            {
                ISqlDatabaseSession session => Opened(Track(databaseId, keyId, Audited(session))),
                DatabaseNotFound => new ResourceNotFound("the database has no local copy on this node", "database_not_local"),
                InvalidDatabaseRequest error => new BackendUnavailable(error.Message, "database_key_mismatch"),
                StorageCorrupted corrupted => new BackendUnavailable(corrupted.Message, "storage_corrupted"),
                StorageBusy busy => new BackendUnavailable(busy.Message, "storage_busy"),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<TenantReadyOutcome> CreateLocalAsync(Ulid databaseId, string keyId, DatabaseKey key, bool strict, CancellationToken cancellationToken)
    {
        var gate = Gate(databaseId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (await UsableAsync(databaseId, keyId) is { } raced)
            {
                return Ready(raced);
            }

            var outcome = await engine.CreateAsync(new DatabaseCreateRequest(databaseId, key.RawKey, strict), cancellationToken);
            return outcome switch
            {
                ISqlDatabaseSession session => Ready(Track(databaseId, keyId, session)),
                DatabaseAlreadyExists => new BackendUnavailable("the database's local copy appeared concurrently; retry", "storage_busy"),
                InvalidDatabaseRequest error => new BackendUnavailable(error.Message, "database_key_invalid"),
                StorageBusy busy => new BackendUnavailable(busy.Message, "storage_busy"),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<ISqlDatabaseSession?> UsableAsync(Ulid databaseId, string keyId)
    {
        ISqlDatabaseSession? stale = null;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(databaseId, out var cached))
            {
                return null;
            }

            if (cached.Session.IsUsable)
            {
                return string.Equals(cached.KeyId, keyId, StringComparison.Ordinal) ? cached.Session : null;
            }

            if (_sessions.TryRemove(databaseId, out var removed))
            {
                stale = removed.Session;
            }
        }

        if (stale is not null)
        {
            await stale.DisposeAsync();
        }

        return null;
    }

    private ISqlDatabaseSession Audited(ISqlDatabaseSession session)
    {
        if (SqliteStorageEngine.TakeRecoveredQuarantined(session) is > 0 and var quarantined)
        {
            TenantLog.RecoveredCommitsQuarantined(logger, session.DatabaseId, quarantined, session.CurrentBookmark);
        }

        return session;
    }

    private ISqlDatabaseSession Track(Ulid databaseId, string keyId, ISqlDatabaseSession session)
    {
        lock (_gate)
        {
            _sessions[databaseId] = new TrackedSession(session, keyId, time.GetTimestamp());
            cluster.TrackDatabase(databaseId);
        }

        return session;
    }

    private TimeSpan IdleFor(TrackedSession tracked) => time.GetElapsedTime(tracked.LastAccessTimestamp);

    private sealed class TrackedSession(ISqlDatabaseSession session, string keyId, long accessedAt)
    {
        private long _lastAccessTimestamp = accessedAt;

        public ISqlDatabaseSession Session { get; } = session;

        public string KeyId { get; } = keyId;

        public long LastAccessTimestamp => Volatile.Read(ref _lastAccessTimestamp);

        public void Touch(long timestamp) => Volatile.Write(ref _lastAccessTimestamp, timestamp);
    }
}
