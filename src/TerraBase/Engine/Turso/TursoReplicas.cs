using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Configuration;
using TerraBase.Engine.Revisions;
using TerraBase.Features.Backup;
using TerraBase.Features.Shapes;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using Turso;
using Turso.Raw.Public;
using Turso.Raw.Public.Handles;
using Turso.Raw.Public.Value;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoReplicas(
    TerraBaseOptions options, [FromKeyedServices(EngineExtensions.DatabaseMasterKey)] IMasterKey master, ILogger<TursoReplicas> logger)
    : IDatabaseReplicas, IAsyncDisposable
{
    internal const string DirectoryName = ".replicas";
    internal const string KeyHeader = "TERRABASE-REPLICA-KEY-1\n";
    private const int MaxIdleConnections = 4;
    private const int MaxReadRows = 10_000;

    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), Lazy<Task<Replica>>> _replicas = new();
    private long _position;
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
        [.. _replicas.Keys.Select(Tenant)];

    private static TenantContext Tenant((string Organization, string Project, string Database) key) => new(key.Organization, PlanTier.Hobby, key.Project, key.Database);

    public DatabaseRevision? HeldRevision(TenantContext database) =>
        _replicas.TryGetValue(Key(database), out var opening) && opening.IsValueCreated && opening.Value.IsCompletedSuccessfully ? opening.Value.Result.Held : null;

    public async ValueTask<ReplicaComparison?> CompareAsync(TenantContext database, DatabaseRevision revision, IReadOnlyList<RangeChecksum> home, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(home);
        return await FindAsync(database).ConfigureAwait(false) is { Pulled: true } replica
            ? await replica.CompareAsync(revision, home, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async ValueTask<IReplicaReader?> BeginReadAsync(TenantContext database, CancellationToken cancellationToken) =>
        await FindAsync(database).ConfigureAwait(false) is { Pulled: true } replica
            ? await replica.BeginReadAsync(cancellationToken).ConfigureAwait(false)
            : null;

    public async ValueTask<IReplicaShapeReader?> BeginShapeReadAsync(TenantContext database, CancellationToken cancellationToken) =>
        await FindAsync(database).ConfigureAwait(false) is { Pulled: true } replica
            ? await TursoReplicaShapeReader.OpenAsync(await replica.ConnectShapeAsync(cancellationToken).ConfigureAwait(false), database, replica.ShapeSchema, cancellationToken).ConfigureAwait(false)
            : null;

    public long Position => Volatile.Read(ref _position);

    public async ValueTask<IReplicaReader?> BeginReadAsync(TenantContext database, long position, CancellationToken cancellationToken) =>
        await FindAsync(database).ConfigureAwait(false) is { Pulled: true } replica && replica.Applied > position
            ? await replica.BeginReadAsync(cancellationToken).ConfigureAwait(false)
            : null;

    private long NextPosition() => Interlocked.Increment(ref _position);

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

    private async Task<Replica> CreateAsync((string Organization, string Project, string Database) key, Uri source, HttpClient http)
    {
        var path = PathOf(key);
        var hexKey = await Task.Run(() => DataKeyRecord(path)).ConfigureAwait(false) switch
        {
            { } wrapped => await UnwrapAsync(wrapped).ConfigureAwait(false),
            null => null,
        };
        var observer = new RevisionObserver(http);
        var observed = new HttpClient(observer) { Timeout = Timeout.InfiniteTimeSpan };
        if (hexKey is not null && File.Exists(path + "-info"))
        {
            try
            {
                return new Replica(this, Tenant(key), await TursoSyncDatabase.OpenAsync(Options(path, hexKey, source, observed)).ConfigureAwait(false), path, observer, observed, logger, pulled: true);
            }
            catch (TursoException error)
            {
                logger.ZLogWarning($"A replica could not be reopened and is started again: {error.GetType().Name:@Error}");
            }
        }

        _ = await Task.Run(() => DeleteFiles(path)).ConfigureAwait(false);
        hexKey = await NewKeyAsync(path).ConfigureAwait(false);
        var replica = new Replica(this, Tenant(key), await TursoSyncDatabase.CreateAsync(Options(path, hexKey, source, observed)).ConfigureAwait(false), path, observer, observed, logger, pulled: false);
        try
        {
            _ = await replica.PullAsync(CancellationToken.None).ConfigureAwait(false);
            return replica;
        }
        catch
        {
            await replica.DisposeAsync().ConfigureAwait(false);
            _ = await Task.Run(() => DeleteFiles(path)).ConfigureAwait(false);
            throw;
        }
    }

    private static TursoSyncDatabaseOptions Options(string path, string hexKey, Uri source, HttpClient http) => new(path, source)
    {
        HttpClient = http,
        ClientName = "terrabase-replica",
        LocalEncryption = new TursoLocalEncryptionOptions { Cipher = TursoRemoteEncryptionCipher.Aegis256, HexKey = hexKey },
    };

    private async Task<string?> UnwrapAsync(string wrapped)
    {
        try
        {
            var key = await master.UnwrapAsync(wrapped, CancellationToken.None).ConfigureAwait(false);
            try
            {
                return key.Length == 32 ? Convert.ToHexString(key) : null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch (CryptographicException)
        {
            logger.ZLogWarning($"A replica's data key is not authentic under this node's master key; the replica is started again");
            return null;
        }
    }

    private async Task<string> NewKeyAsync(string path)
    {
        var (key, wrapped) = await master.GenerateDataKeyAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                DatabaseKeys.RejectLink(path + DatabaseKeys.Suffix);
                var temporary = path + DatabaseKeys.Suffix + ".tmp";
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    file.Write(Encoding.ASCII.GetBytes(KeyHeader + wrapped + "\n"));
                    file.Flush(flushToDisk: true);
                }

                File.Move(temporary, path + DatabaseKeys.Suffix, overwrite: true);
            }).ConfigureAwait(false);
            return Convert.ToHexString(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string? DataKeyRecord(string path)
    {
        var file = path + DatabaseKeys.Suffix;
        DatabaseKeys.RejectLink(file);
        if (!File.Exists(file) || new FileInfo(file).Length > DatabaseKeys.MaxFileBytes)
        {
            return null;
        }

        var text = File.ReadAllText(file, Encoding.ASCII);
        return text.StartsWith(KeyHeader, StringComparison.Ordinal) && text.EndsWith('\n') && text.Length > KeyHeader.Length + 1
            ? text[KeyHeader.Length..^1]
            : null;
    }

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

    private sealed class RevisionObserver(HttpClient http) : HttpMessageHandler
    {
        private string? _seen;

        public string? Take() => Interlocked.Exchange(ref _seen, null);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var copy = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = request.Content,
                Version = request.Version,
                VersionPolicy = request.VersionPolicy,
            };
            foreach (var (name, values) in request.Headers)
            {
                _ = copy.Headers.TryAddWithoutValidation(name, values);
            }

            try
            {
                var response = await http.SendAsync(copy, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && response.Headers.TryGetValues(DatabaseRevision.Header, out var values))
                {
                    Volatile.Write(ref _seen, values.FirstOrDefault());
                }

                return response;
            }
            finally
            {
                copy.Content = null;
            }
        }
    }

    private sealed partial class Replica(TursoReplicas owner, TenantContext database, TursoSyncDatabase sync, string path, RevisionObserver observer, HttpClient observed, ILogger logger, bool pulled) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _pulls = new(1, 1);
        private readonly ReplicaGate _gate = new();
        private readonly ConcurrentQueue<TursoDatabaseHandle> _idle = new();
        private int _idleCount;
        private int _disposed;

        public bool Pulled { get; private set; } = pulled;

        internal TursoReplicaShapeSchema ShapeSchema { get; } = new();

        public long Applied => Volatile.Read(ref _applied);

        private long _applied;

        public DatabaseRevision? Held { get; private set; }

        public async Task<ReplicaComparison> CompareAsync(DatabaseRevision revision, IReadOnlyList<RangeChecksum> home, CancellationToken cancellationToken)
        {
            await _pulls.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Held != revision)
                {
                    return new ReplicaComparison(ReplicaVerdict.Moved, Held, null);
                }

                await using var reader = await BeginReadAsync(cancellationToken).ConfigureAwait(false);
                foreach (var range in home)
                {
                    RangeChecksum? local;
                    try
                    {
                        local = await ChecksumAsync(reader, range, cancellationToken).ConfigureAwait(false);
                    }
                    catch (SqlStatementException)
                    {
                        local = null;
                    }

                    if (local != range)
                    {
                        return new ReplicaComparison(ReplicaVerdict.Diverged, revision, RangeChecksums.Mismatch(range, local));
                    }
                }

                return new ReplicaComparison(ReplicaVerdict.Equal, revision, null);
            }
            finally
            {
                _pulls.Release();
            }
        }

        private static async Task<RangeChecksum?> ChecksumAsync(IReplicaReader reader, RangeChecksum home, CancellationToken cancellationToken)
        {
            var schema = home.Table.Length == 0;
            var sql = schema ? RangeChecksums.SchemaSql : RangeChecksums.RowsSql(home.Table, home.After, home.Last, home.Rows + 1);
            var maxRows = schema ? MaxReadRows : home.Rows + 1;
            var result = await reader.ExecuteAsync(new SqlStatement(sql, [], new Dictionary<string, object?>(), maxRows, home.Bytes + RangeChecksums.RangeBytes, TimeSpan.FromSeconds(30)),
                cancellationToken).ConfigureAwait(false);
            if (result.Truncated)
            {
                return null;
            }

            using var hasher = new RangeChecksums.Hasher();
            foreach (var row in result.Rows)
            {
                hasher.Add(row);
            }

            return hasher.Finish(home.Table, home.After, home.Last);
        }

        public async Task<ReplicaPullResult> PullAsync(CancellationToken cancellationToken)
        {
            await _pulls.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var position = owner.NextPosition();
                _ = observer.Take();
                bool changed;
                using (await _gate.ExclusiveAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        changed = await sync.PullAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        Held = null;
                        throw;
                    }

                    Held = DatabaseRevision.TryParse(observer.Take(), out var pulledRevision) ? pulledRevision : null;
                    if (changed)
                    {
                        await RestoreOverlayAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                Volatile.Write(ref _applied, position);
                Pulled = true;
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

        internal Task<TursoConnection> ConnectShapeAsync(CancellationToken cancellationToken) => sync.ConnectAsync(cancellationToken);

        public async Task<IReplicaReader> BeginReadAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var lease = await _gate.ReadAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await OpenReaderAsync(lease, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        private async Task<IReplicaReader> OpenReaderAsync(IDisposable lease, CancellationToken cancellationToken)
        {
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

            return new Reader(this, native, lease);
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

            if (_writer is { } writer)
            {
                _writer = null;
                Close(writer);
            }

            sync.Dispose();
            observed.Dispose();
            _pulls.Dispose();
            _gate.Dispose();
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

        private sealed class Reader(Replica replica, TursoDatabaseHandle native, IDisposable lease) : IReplicaReader
        {
            private bool _healthy = true;
            private int _disposed;

            public ValueTask<SqlStatementResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(statement);
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return new(Task.Run(() => Execute(statement, cancellationToken), cancellationToken));
            }

            public ValueTask<SqlStatementShape> DescribeAsync(string sql, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(sql);
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return new(Task.Run(() => Describe(sql), cancellationToken));
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    replica.Return(native, _healthy);
                    lease.Dispose();
                }

                return ValueTask.CompletedTask;
            }

            private SqlStatementShape Describe(string sql)
            {
                try
                {
                    var (prepared, _) = TursoStorageTransaction.Prepare(native, sql, first: false);
                    try
                    {
                        var parameters = new string?[TursoBindings.GetParameterCount(prepared)];
                        for (var i = 0; i < parameters.Length; i++)
                        {
                            parameters[i] = TursoBindings.GetParameterName(prepared, i + 1);
                        }

                        return new SqlStatementShape(parameters, TursoStorageTransaction.Columns(prepared));
                    }
                    finally
                    {
                        TursoStorageTransaction.Release(prepared);
                    }
                }
                catch (TursoException error)
                {
                    var (failure, message) = TursoStorageTransaction.Classify(native, error);
                    _healthy = failure is not SqlFailure.Error;
                    throw new SqlStatementException(failure, message, transactionEnded: false, error);
                }
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
                            var (columns, rows, _, truncated) = Step(prepared, statement, cancellationToken);
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

            internal static (List<SqlStatementColumn> Columns, List<object?[]> Rows, long Affected, bool Truncated) Step(
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
                            return (columns, rows, TursoBindings.RowsAffected(prepared), true);
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

                    return (columns, rows, TursoBindings.RowsAffected(prepared), false);
                }
                finally
                {
                    TursoStorageTransaction.Release(prepared);
                }
            }
        }
    }
}
