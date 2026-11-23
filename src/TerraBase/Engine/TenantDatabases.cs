using TerraScale.Contracts.Cluster;

namespace TerraBase.Engine;

public sealed record OpenedDatabase(ISqlDatabaseSession Session, string KeyId);

internal sealed class TenantDatabases(ISqlStorageEngine engine, DatabaseKeyring keyring, IClusterCoordinator cluster) : IAsyncDisposable
{
    private static readonly Unavailable MissingKey = new("the database's master key is not configured on this node", "master_key_missing");

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _opening = new(1, 1);
    private readonly Dictionary<Ulid, ISqlDatabaseSession> _sessions = [];

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

    public async ValueTask<Result<OpenedDatabase, Conflict, Unavailable>> CreateAsync(Ulid databaseId, bool strict, CancellationToken cancellationToken)
    {
        var key = keyring.DeriveActive(databaseId);
        await _opening.WaitAsync(cancellationToken);
        try
        {
            return await engine.CreateAsync(new DatabaseCreateRequest(databaseId, key.RawKey, strict), cancellationToken) switch
            {
                ISqlDatabaseSession session => new OpenedDatabase(Track(databaseId, session), key.KeyId),
                Conflict conflict => conflict,
                Validation error => new Unavailable(error.Message, "database_key_invalid"),
                StorageBusy busy => new Unavailable(busy.Message, "storage_busy"),
            };
        }
        finally
        {
            _opening.Release();
        }
    }

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

    public async ValueTask<Result<OpenedDatabase, NotFound, Conflict, Unavailable>> BranchAsync(
        ISqlDatabaseSession source, Ulid branchId, CausalBookmark at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var key = keyring.DeriveActive(branchId);
        await _opening.WaitAsync(cancellationToken);
        try
        {
            return await engine.BranchAsync(new DatabaseBranchRequest(source.DatabaseId, branchId, key.RawKey, at), cancellationToken) switch
            {
                ISqlDatabaseSession session => new OpenedDatabase(Track(branchId, session), key.KeyId),
                NotFound error => error,
                Conflict conflict => conflict,
                Validation error => new Conflict(error.Message, "branch_point_invalid"),
                StorageBusy busy => new Unavailable(busy.Message, "storage_busy"),
            };
        }
        finally
        {
            _opening.Release();
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

        _opening.Dispose();
    }

    private static Result<ISqlDatabaseSession, NotFound, Unavailable> Opened(ISqlDatabaseSession session) => new(session);

    private static Result<ISqlDatabaseSession, Unavailable> Ready(ISqlDatabaseSession session) => new(session);

    private async ValueTask<Result<ISqlDatabaseSession, NotFound, Unavailable>> OpenWithKeyAsync(Ulid databaseId, DatabaseKey key, CancellationToken cancellationToken)
    {
        await _opening.WaitAsync(cancellationToken);
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
            _opening.Release();
        }
    }

    private async ValueTask<Result<ISqlDatabaseSession, Unavailable>> CreateLocalAsync(Ulid databaseId, DatabaseKey key, bool strict, CancellationToken cancellationToken)
    {
        await _opening.WaitAsync(cancellationToken);
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
            _opening.Release();
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
