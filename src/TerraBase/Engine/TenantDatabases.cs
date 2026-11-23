using System.Collections.Concurrent;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Engine;

internal sealed class TenantDatabases(ISqlStorageEngine engine, DatabaseKeyring keyring, IClusterCoordinator cluster) : IAsyncDisposable
{
    private static readonly Unavailable MissingKey = new("the database's master key is not configured on this node", "master_key_missing");

    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<Ulid, SemaphoreSlim> _opening = new();
    private readonly Dictionary<Ulid, ISqlDatabaseSession> _sessions = [];

    public string ActiveKeyId => keyring.ActiveKeyId;

    public async ValueTask<Result<ISqlDatabaseSession, NotFound, Unavailable>> OpenAsync(Ulid databaseId, string keyId, CancellationToken cancellationToken)
    {
        if (Cached(databaseId) is { } cached)
        {
            return Opened(cached);
        }

        return keyring.Derive(databaseId, keyId) switch
        {
            DatabaseKey key => await OpenWithKeyAsync(databaseId, key, cancellationToken),
            NotFound => MissingKey,
        };
    }

    public async ValueTask<Result<ISqlDatabaseSession, Conflict, Unavailable>> CreateAsync(Ulid databaseId, string keyId, bool strict, CancellationToken cancellationToken) =>
        keyring.Derive(databaseId, keyId) switch
        {
            DatabaseKey key => await CreateWithKeyAsync(databaseId, key, strict, cancellationToken),
            NotFound => MissingKey,
        };

    public async ValueTask<Result<ISqlDatabaseSession, Unavailable>> OpenOrCreateAsync(Ulid databaseId, string keyId, bool strict, CancellationToken cancellationToken) =>
        await OpenAsync(databaseId, keyId, cancellationToken) switch
        {
            ISqlDatabaseSession existing => Ready(existing),
            NotFound => keyring.Derive(databaseId, keyId) switch
            {
                DatabaseKey key => await CreateLocalAsync(databaseId, key, strict, cancellationToken),
                NotFound => MissingKey,
            },
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<ISqlDatabaseSession, NotFound, Conflict, Unavailable>> BranchAsync(
        ISqlDatabaseSession source, Ulid branchId, string keyId, CausalBookmark at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return keyring.Derive(branchId, keyId) switch
        {
            DatabaseKey key => await BranchWithKeyAsync(source, branchId, key, at, cancellationToken),
            NotFound => MissingKey,
        };
    }

    private async ValueTask<Result<ISqlDatabaseSession, NotFound, Conflict, Unavailable>> BranchWithKeyAsync(
        ISqlDatabaseSession source, Ulid branchId, DatabaseKey key, CausalBookmark at, CancellationToken cancellationToken)
    {
        var gate = Gate(branchId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await engine.BranchAsync(new DatabaseBranchRequest(source.DatabaseId, branchId, key.RawKey, at), cancellationToken) switch
            {
                ISqlDatabaseSession session => Branched(Track(branchId, session)),
                NotFound error => error,
                Conflict conflict => conflict,
                Validation error => new Conflict(error.Message, "branch_point_invalid"),
                StorageBusy busy => new Unavailable(busy.Message, "storage_busy"),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<Result<Done, Unavailable>> DeleteAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        await EvictAsync(databaseId);
        return await engine.CryptoShredAsync(databaseId, cancellationToken) switch
        {
            Done or NotFound => new Done(),
            StorageBusy busy => new Unavailable(busy.Message, "storage_busy"),
        };
    }

    public async ValueTask EvictAsync(Ulid databaseId)
    {
        ISqlDatabaseSession? session;
        lock (_gate)
        {
            _sessions.Remove(databaseId, out session);
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

    private static Result<ISqlDatabaseSession, NotFound, Unavailable> Opened(ISqlDatabaseSession session) => new(session);

    private static Result<ISqlDatabaseSession, Unavailable> Ready(ISqlDatabaseSession session) => new(session);

    private static Result<ISqlDatabaseSession, NotFound, Conflict, Unavailable> Branched(ISqlDatabaseSession session) => new(session);

    private static Result<ISqlDatabaseSession, Conflict, Unavailable> Created(ISqlDatabaseSession session) => new(session);

    private SemaphoreSlim Gate(Ulid databaseId) => _opening.GetOrAdd(databaseId, static _ => new SemaphoreSlim(1, 1));

    private async ValueTask<Result<ISqlDatabaseSession, Conflict, Unavailable>> CreateWithKeyAsync(Ulid databaseId, DatabaseKey key, bool strict, CancellationToken cancellationToken)
    {
        var gate = Gate(databaseId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await engine.CreateAsync(new DatabaseCreateRequest(databaseId, key.RawKey, strict), cancellationToken) switch
            {
                ISqlDatabaseSession session => Created(Track(databaseId, session)),
                Conflict conflict => conflict,
                Validation error => new Unavailable(error.Message, "database_key_invalid"),
                StorageBusy busy => new Unavailable(busy.Message, "storage_busy"),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<Result<ISqlDatabaseSession, NotFound, Unavailable>> OpenWithKeyAsync(Ulid databaseId, DatabaseKey key, CancellationToken cancellationToken)
    {
        var gate = Gate(databaseId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Cached(databaseId) is { } raced)
            {
                return Opened(raced);
            }

            return await engine.OpenAsync(new DatabaseOpenRequest(databaseId, key.RawKey), cancellationToken) switch
            {
                ISqlDatabaseSession session => Opened(Track(databaseId, session)),
                NotFound => new NotFound("the database has no local copy on this node", "database_not_local"),
                Validation error => new Unavailable(error.Message, "database_key_mismatch"),
                StorageCorrupted corrupted => new Unavailable(corrupted.Message, "storage_corrupted"),
                StorageBusy busy => new Unavailable(busy.Message, "storage_busy"),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<Result<ISqlDatabaseSession, Unavailable>> CreateLocalAsync(Ulid databaseId, DatabaseKey key, bool strict, CancellationToken cancellationToken)
    {
        var gate = Gate(databaseId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Cached(databaseId) is { } raced)
            {
                return Ready(raced);
            }

            return await engine.CreateAsync(new DatabaseCreateRequest(databaseId, key.RawKey, strict), cancellationToken) switch
            {
                ISqlDatabaseSession session => Ready(Track(databaseId, session)),
                Conflict => new Unavailable("the database's local copy appeared concurrently; retry", "storage_busy"),
                Validation error => new Unavailable(error.Message, "database_key_invalid"),
                StorageBusy busy => new Unavailable(busy.Message, "storage_busy"),
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
        }

        cluster.TrackDatabase(databaseId);
        return session;
    }
}
