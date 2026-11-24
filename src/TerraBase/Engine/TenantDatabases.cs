using System.Collections.Concurrent;
using TerraBase.Hosting;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Engine;

internal readonly union TenantOpenOutcome(ISqlDatabaseSession, ResourceNotFound, BackendUnavailable);

internal readonly union TenantCreateOutcome(ISqlDatabaseSession, StateConflict, BackendUnavailable);

internal readonly union TenantReadyOutcome(ISqlDatabaseSession, BackendUnavailable);

internal readonly union TenantBranchOutcome(ISqlDatabaseSession, ResourceNotFound, StateConflict, BackendUnavailable);

internal readonly union TenantDeleteOutcome(Completed, BackendUnavailable);

internal sealed class TenantDatabases(ISqlStorageEngine engine, DatabaseKeyring keyring, IClusterCoordinator cluster) : IAsyncDisposable
{
    private static readonly BackendUnavailable MissingKey = new("the database's master key is not configured on this node", "master_key_missing");

    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<Ulid, SemaphoreSlim> _opening = new();
    private readonly Dictionary<Ulid, ISqlDatabaseSession> _sessions = [];

    public string ActiveKeyId => keyring.ActiveKeyId;

    public async ValueTask<TenantOpenOutcome> OpenAsync(Ulid databaseId, string keyId, CancellationToken cancellationToken)
    {
        if (Cached(databaseId) is { } cached)
        {
            return Opened(cached);
        }

        return keyring.Derive(databaseId, keyId) switch
        {
            DatabaseKey key => await OpenWithKeyAsync(databaseId, key, cancellationToken),
            MasterKeyMissing => MissingKey,
        };
    }

    public async ValueTask<TenantCreateOutcome> CreateAsync(Ulid databaseId, string keyId, bool strict, CancellationToken cancellationToken) =>
        keyring.Derive(databaseId, keyId) switch
        {
            DatabaseKey key => await CreateWithKeyAsync(databaseId, key, strict, cancellationToken),
            MasterKeyMissing => MissingKey,
        };

    public async ValueTask<TenantReadyOutcome> OpenOrCreateAsync(Ulid databaseId, string keyId, bool strict, CancellationToken cancellationToken)
    {
        var outcome = await OpenAsync(databaseId, keyId, cancellationToken);
        return outcome switch
        {
            ISqlDatabaseSession existing => Ready(existing),
            ResourceNotFound => keyring.Derive(databaseId, keyId) switch
            {
                DatabaseKey key => await CreateLocalAsync(databaseId, key, strict, cancellationToken),
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
            DatabaseKey key => await BranchWithKeyAsync(source, branchId, key, at, cancellationToken),
            MasterKeyMissing => MissingKey,
        };
    }

    private async ValueTask<TenantBranchOutcome> BranchWithKeyAsync(
        ISqlDatabaseSession source, Ulid branchId, DatabaseKey key, CausalBookmark at, CancellationToken cancellationToken)
    {
        var gate = Gate(branchId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var outcome = await engine.BranchAsync(new DatabaseBranchRequest(source.DatabaseId, branchId, key.RawKey, at), cancellationToken);
            return outcome switch
            {
                ISqlDatabaseSession session => Branched(Track(branchId, session)),
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
        ISqlDatabaseSession? session;
        lock (_gate)
        {
            _sessions.Remove(databaseId, out session);
            cluster.UntrackDatabase(databaseId);
        }

        if (session is not null)
        {
            await session.DisposeAsync();
        }
    }

    public IReadOnlyList<ISqlDatabaseSession> OpenSessions()
    {
        lock (_gate)
        {
            return [.. _sessions.Values];
        }
    }

    public async ValueTask DisposeAsync()
    {
        ISqlDatabaseSession[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions.Values];
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

    private async ValueTask<TenantCreateOutcome> CreateWithKeyAsync(Ulid databaseId, DatabaseKey key, bool strict, CancellationToken cancellationToken)
    {
        var gate = Gate(databaseId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var outcome = await engine.CreateAsync(new DatabaseCreateRequest(databaseId, key.RawKey, strict), cancellationToken);
            return outcome switch
            {
                ISqlDatabaseSession session => Created(Track(databaseId, session)),
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

    private async ValueTask<TenantOpenOutcome> OpenWithKeyAsync(Ulid databaseId, DatabaseKey key, CancellationToken cancellationToken)
    {
        var gate = Gate(databaseId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Cached(databaseId) is { } raced)
            {
                return Opened(raced);
            }

            var outcome = await engine.OpenAsync(new DatabaseOpenRequest(databaseId, key.RawKey), cancellationToken);
            return outcome switch
            {
                ISqlDatabaseSession session => Opened(Track(databaseId, session)),
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

    private async ValueTask<TenantReadyOutcome> CreateLocalAsync(Ulid databaseId, DatabaseKey key, bool strict, CancellationToken cancellationToken)
    {
        var gate = Gate(databaseId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Cached(databaseId) is { } raced)
            {
                return Ready(raced);
            }

            var outcome = await engine.CreateAsync(new DatabaseCreateRequest(databaseId, key.RawKey, strict), cancellationToken);
            return outcome switch
            {
                ISqlDatabaseSession session => Ready(Track(databaseId, session)),
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

    private ISqlDatabaseSession? Cached(Ulid databaseId)
    {
        lock (_gate)
        {
            return _sessions.GetValueOrDefault(databaseId);
        }
    }

    private ISqlDatabaseSession Track(Ulid databaseId, ISqlDatabaseSession session)
    {
        lock (_gate)
        {
            _sessions[databaseId] = session;
            cluster.TrackDatabase(databaseId);
        }

        return session;
    }
}
