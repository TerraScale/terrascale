using System.Collections.Concurrent;
using TerraBase.Configuration;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using Turso;
using Turso.Raw.Public;
using Turso.Raw.Public.Handles;
using Turso.Raw.Public.Value;
using ZLogger;

namespace TerraBase.Engine.Turso;

/// <summary>
/// Region replicas on Turso's official sync client in page mode (docs/terrabase/region-replicas.md). Each replica is one
/// file under <c>.replicas/&lt;organization&gt;/&lt;project&gt;/&lt;database&gt;.db</c>; only pulls write it. Replica files are
/// not encrypted at rest yet: the sync client writes its WAL and staged answers without its page transform.
/// </summary>
internal sealed class TursoReplicas(TerraBaseOptions options, ILogger<TursoReplicas> logger) : IDatabaseReplicas, IAsyncDisposable
{
    internal const string DirectoryName = ".replicas";
    private const int MaxIdleConnections = 4;
    private const int MaxReadRows = 10_000;

    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), Lazy<Task<Replica>>> _replicas = new();
    private int _disposed;

    private string Root => Path.Combine(options.DataDirectory, DirectoryName);

    public async ValueTask OpenAsync(TenantContext database, Uri source, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(http);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var key = Key(database);
        var opening = _replicas.GetOrAdd(key, _ => new Lazy<Task<Replica>>(() => CreateAsync(key, source, http), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            _ = await opening.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A failed open is retried by the next call, not cached.
            _ = _replicas.TryRemove(new KeyValuePair<(string, string, string), Lazy<Task<Replica>>>(key, opening));
            throw;
        }
    }

    public async ValueTask<ReplicaPullResult?> PullAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (await FindAsync(database).ConfigureAwait(false) is not { } replica)
        {
            return null;
        }

        return await replica.PullAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> DeleteAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var key = Key(database);
        if (_replicas.TryRemove(key, out var opening))
        {
            try
            {
                var replica = await opening.Value.ConfigureAwait(false);
                await replica.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.ZLogWarning($"A replica that failed to open is deleted: {error.GetType().Name:@Error}");
            }
        }

        return await Task.Run(() => DeleteFiles(PathOf(key)), cancellationToken).ConfigureAwait(false);
    }

    public bool Holds(TenantContext database) =>
        database.ProjectId is { } project && database.DatabaseId is { } id && _replicas.ContainsKey((database.OrganizationId, project, id));

    public IReadOnlyList<TenantContext> List() =>
        [.. _replicas.Keys.Select(key => new TenantContext(key.Organization, PlanTier.Hobby, key.Project, key.Database))];

    public async ValueTask<IReplicaReader?> BeginReadAsync(TenantContext database, CancellationToken cancellationToken) =>
        await FindAsync(database).ConfigureAwait(false) is { Pulled: true } replica
            ? await replica.BeginReadAsync(cancellationToken).ConfigureAwait(false)
            : null;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var key in _replicas.Keys)
        {
            if (_replicas.TryRemove(key, out var opening) && opening.IsValueCreated)
            {
                try
                {
                    await (await opening.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.ZLogWarning($"A replica failed to close: {error.GetType().Name:@Error}");
                }
            }
        }
    }

    private async ValueTask<Replica?> FindAsync(TenantContext database)
    {
        if (!_replicas.TryGetValue(Key(database), out var opening) || !opening.IsValueCreated)
        {
            return null;
        }

        try
        {
            return await opening.Value.ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private static (string Organization, string Project, string Database) Key(TenantContext database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return EntityIds.IsValid(database.OrganizationId) && EntityIds.IsValid(database.ProjectId) && EntityIds.IsValid(database.DatabaseId)
            ? (database.OrganizationId, database.ProjectId, database.DatabaseId)
            : throw new ArgumentException("a replica is addressed by its organization, project and database ULIDs", nameof(database));
    }

    private string PathOf((string Organization, string Project, string Database) key) =>
        Path.Combine(Root, key.Organization, key.Project, key.Database + ".db");

    // Opens the replica that exists, otherwise starts one from the home's first full answer. A replica whose sync state is
    // unreadable is deleted and started again: the home holds every page it needs.
    private async Task<Replica> CreateAsync((string Organization, string Project, string Database) key, Uri source, HttpClient http)
    {
        var path = PathOf(key);
        if (File.Exists(path + "-info"))
        {
            try
            {
                return new Replica(await TursoSyncDatabase.OpenAsync(Options(path, source, http)).ConfigureAwait(false), path, logger, pulled: true);
            }
            catch (TursoException error)
            {
                logger.ZLogWarning($"A replica could not be reopened and is started again: {error.GetType().Name:@Error}");
            }
        }

        _ = await Task.Run(() => DeleteFiles(path)).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var replica = new Replica(await TursoSyncDatabase.CreateAsync(Options(path, source, http)).ConfigureAwait(false), path, logger, pulled: false);
        try
        {
            _ = await replica.PullAsync(CancellationToken.None).ConfigureAwait(false);
            return replica;
        }
        catch
        {
            // A replica is reopened only after its first full pull; without one it starts again from nothing.
            await replica.DisposeAsync().ConfigureAwait(false);
            _ = await Task.Run(() => DeleteFiles(path)).ConfigureAwait(false);
            throw;
        }
    }

    private static TursoSyncDatabaseOptions Options(string path, Uri source, HttpClient http) => new(path, source)
    {
        HttpClient = http,
        ClientName = "terrabase-replica",
        // The first answer is a full one either way; taking it as a pull keeps one apply path for every answer.
        BootstrapIfEmpty = false,
    };

    private static bool DeleteFiles(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            return false;
        }

        DatabaseKeys.RejectLink(directory);
        var name = Path.GetFileName(path);
        var deleted = false;
        foreach (var file in Directory.EnumerateFiles(directory, name + "*"))
        {
            File.Delete(file);
            deleted = true;
        }

        return deleted;
    }

    private sealed class Replica(TursoSyncDatabase sync, string path, ILogger logger, bool pulled) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _pulls = new(1, 1);
        private readonly ConcurrentQueue<TursoDatabaseHandle> _idle = new();
        private int _idleCount;
        private int _disposed;

        // The client's statistics read its local change table, which a replica that never writes does not have, so readiness
        // is the first applied pull of this replica's files.
        public bool Pulled { get; private set; } = pulled;

        public async Task<ReplicaPullResult> PullAsync(CancellationToken cancellationToken)
        {
            await _pulls.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var changed = await sync.PullAsync(cancellationToken).ConfigureAwait(false);
                Pulled = true;
                // The replica's own log only holds pulled pages; fold it into the file once it outgrows the home's policy.
                var log = new FileInfo(path + "-wal");
                if (log.Exists && log.Length > 4L * 1024 * 1024)
                {
                    try
                    {
                        await sync.CheckpointAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (TursoException error)
                    {
                        logger.ZLogInformation($"A replica checkpoint waits for its readers: {error.GetType().Name:@Error}");
                    }
                }

                return new ReplicaPullResult(changed);
            }
            finally
            {
                _pulls.Release();
            }
        }

        public async Task<IReplicaReader> BeginReadAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_idle.TryDequeue(out var native))
            {
                native = await sync.ConnectHandleAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    Run(native, "PRAGMA query_only = 1;");
                }
                catch
                {
                    Close(native);
                    throw;
                }
            }
            else
            {
                _ = Interlocked.Decrement(ref _idleCount);
            }

            try
            {
                Run(native, "BEGIN;");
            }
            catch
            {
                Close(native);
                throw;
            }

            return new Reader(this, native);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            while (_idle.TryDequeue(out var native))
            {
                Close(native);
            }

            sync.Dispose();
            _pulls.Dispose();
            return ValueTask.CompletedTask;
        }

        private void Return(TursoDatabaseHandle native, bool healthy)
        {
            try
            {
                Run(native, "ROLLBACK;");
            }
            catch (TursoException)
            {
                healthy = false;
            }

            if (healthy && Volatile.Read(ref _disposed) == 0 && Interlocked.Increment(ref _idleCount) <= MaxIdleConnections)
            {
                _idle.Enqueue(native);
                return;
            }

            if (healthy && Volatile.Read(ref _disposed) == 0)
            {
                _ = Interlocked.Decrement(ref _idleCount);
            }

            Close(native);
        }

        private void Close(TursoDatabaseHandle native)
        {
            native.Dispose();
            sync.ReleaseConnection();
        }

        private static void Run(TursoDatabaseHandle native, string sql)
        {
            var prepared = TursoBindings.PrepareStatement(native, sql);
            try
            {
                while (TursoBindings.Read(prepared))
                {
                }
            }
            finally
            {
                TursoStorageTransaction.Release(prepared);
            }
        }

        private sealed class Reader(Replica replica, TursoDatabaseHandle native) : IReplicaReader
        {
            private bool _healthy = true;
            private int _disposed;

            public ValueTask<SqlStatementResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(statement);
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return new(Task.Run(() => Execute(statement, cancellationToken), cancellationToken));
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    replica.Return(native, _healthy);
                }

                return ValueTask.CompletedTask;
            }

            private SqlStatementResult Execute(SqlStatement statement, CancellationToken cancellationToken)
            {
                try
                {
                    var (prepared, consumed) = TursoStorageTransaction.Prepare(native, statement.Sql, statement.First);
                    TursoBindings.SetQueryTimeout(native, statement.Timeout);
                    TursoBindings.SetMaxValueLength(native, statement.MaxBytes);
                    try
                    {
                        using (cancellationToken.Register(static state => TursoBindings.Interrupt((TursoDatabaseHandle)state!), native))
                        {
                            var (columns, rows, truncated) = Step(prepared, statement, cancellationToken);
                            return new SqlStatementResult(columns, rows, 0, 0, 0, truncated, consumed);
                        }
                    }
                    finally
                    {
                        TursoBindings.SetQueryTimeout(native, TimeSpan.Zero);
                        TursoBindings.SetMaxValueLength(native, long.MaxValue);
                        TursoBindings.ClearInterrupt(native);
                    }
                }
                catch (TursoException error)
                {
                    var (failure, message) = TursoStorageTransaction.Classify(native, error);
                    _healthy = failure is not SqlFailure.Error;
                    throw new SqlStatementException(failure, message, transactionEnded: false, error);
                }
            }

            private static (List<SqlStatementColumn> Columns, List<object?[]> Rows, bool Truncated) Step(
                TursoStatementHandle prepared, SqlStatement statement, CancellationToken cancellationToken)
            {
                try
                {
                    TursoStorageTransaction.Bind(prepared, statement);
                    var columns = TursoStorageTransaction.Columns(prepared);
                    var rows = new List<object?[]>();
                    long bytes = 0;
                    var maxRows = Math.Min(statement.MaxRows, MaxReadRows);
                    while (TursoBindings.Read(prepared))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        long size = 0;
                        for (var i = 0; i < columns.Count; i++)
                        {
                            size += TursoBindings.GetValueType(prepared, i) switch
                            {
                                TursoValueType.Text or TursoValueType.Blob => Math.Max(0, TursoBindings.GetValueByteCount(prepared, i)),
                                TursoValueType.Null or TursoValueType.Empty => 0,
                                _ => 8,
                            };
                        }

                        if (rows.Count == maxRows || size > statement.MaxBytes - bytes)
                        {
                            return (columns, rows, true);
                        }

                        bytes += size;
                        var row = new object?[columns.Count];
                        for (var i = 0; i < row.Length; i++)
                        {
                            row[i] = TursoBindings.GetValue(prepared, i) switch
                            {
                                { ValueType: TursoValueType.Integer } value => value.IntValue,
                                { ValueType: TursoValueType.Real } value => value.RealValue,
                                { ValueType: TursoValueType.Text } value => value.StringValue,
                                { ValueType: TursoValueType.Blob } value => value.BlobValue,
                                _ => null,
                            };
                        }

                        rows.Add(row);
                    }

                    return (columns, rows, false);
                }
                finally
                {
                    TursoStorageTransaction.Release(prepared);
                }
            }
        }
    }
}
