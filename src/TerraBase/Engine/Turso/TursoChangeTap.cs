using System.Buffers;
using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Turso;
using Turso.Data.Sqlite;
using Turso.Raw.Public;

namespace TerraBase.Engine.Turso;


internal sealed partial class TursoChangeTap : IWalTap, ICdcBackupRetention
{

    private const int PageRows = 1024;
    private const int PageBytes = 4 << 20;

    private const long ReadBudget = 32L << 20;

    private readonly TursoDatabaseStore _store;
    private readonly TursoIoPool _io;
    private readonly TimeProvider _time;

    private TursoConnection _reader;
    private int _disposed;

    private readonly int _lineage;

    private long _anchor;
    private long _durableAnchor;
    private long _storedAnchor;

    public long Position => Volatile.Read(ref _anchor);

    public async ValueTask<long> RetainedBytesAsync(CancellationToken cancellationToken) =>
        await AdmittedAsync(async () => await _io.RunAsync(() =>
        {
            using var command = _reader.CreateCommand();
            command.CommandText = "SELECT coalesce(sum(coalesce(octet_length(before), 0) + coalesce(octet_length(after), 0) + 32), 0) FROM turso_cdc WHERE change_id > $anchor;";
            command.Parameters.Add(new TursoParameter("$anchor", Volatile.Read(ref _storedAnchor)));
            return (long)command.ExecuteScalar()!;
        }, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    public async ValueTask AcknowledgeAsync(long position, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        await _store.WithWriterAsync(writer =>
        {
            ObjectDisposedException.ThrowIf(_lineage != _store.Lineage, _store);
            if (position > _anchor)
            {
                throw new InvalidOperationException("the backup cannot acknowledge unread CDC");
            }

            _durableAnchor = Math.Max(_durableAnchor, position);
            Volatile.Write(ref _storedAnchor, Math.Max(_storedAnchor, position));
            _store.BackupAnchor = _durableAnchor;
            return Truncate(writer);
        }, "the durable backup CDC acknowledgement", cancellationToken).ConfigureAwait(false);
    }

    private Dictionary<long, (string Type, string Name)> _schema = [];

    private (long First, long Latest, bool Ordered)? _open;

    private TursoChangeTap(TursoDatabaseStore store, TursoIoPool io, TimeProvider time, TursoConnection reader, int lineage)
    {
        _store = store;
        _io = io;
        _time = time;
        _reader = reader;
        _lineage = lineage;
    }

    public static async Task<TursoChangeTap> OpenAsync(TursoDatabaseStore store, TursoIoPool io, TimeProvider time, CancellationToken cancellationToken)
    {
        await using var operation = await store.BeginOperationAsync(cancellationToken).ConfigureAwait(false);
        var connectionString = await store.ConnectionStringAsync(io, cancellationToken).ConfigureAwait(false);
        TursoChangeTap? tap = null;
        try
        {

            await store.WithWriterAsync(writer =>
            {
                var lineage = store.Lineage;
                if (store.Quiescing)
                {

                    throw new InvalidOperationException("a healthy rewrite of this database waits for its handles; the tap opens after it");
                }

                store.OpenBackupReader();
                Interlocked.Increment(ref store.BackupTaps);
                var reader = new TursoConnection(connectionString) { CloseRefusal = () => store.CloseRefusal ?? "terminal backup reader close" };
                tap = new TursoChangeTap(store, io, time, reader, lineage);
                TursoSession.OpenExisting(reader, store.FilePath);
                return StartHistory(writer) && tap.Reset(writer);
            }, "the continuous backup's tap", cancellationToken).ConfigureAwait(false);
            operation?.Complete();
            return tap!;
        }
        catch (Exception error)
        {
            if (tap is not null)
            {
                await io.RunAsync(tap._reader.Dispose, CancellationToken.None).ConfigureAwait(false);
                store.CloseBackupReader();
                Interlocked.Decrement(ref store.BackupTaps);
            }

            await store.RecordOperationDamageAsync(error).ConfigureAwait(false);
            operation?.Complete();
            throw;
        }
    }

    public bool Ended => _store.IsClosed || Volatile.Read(ref _disposed) != 0 || _lineage != _store.Lineage;

    public async ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return await AdmittedAsync(async () =>
        {
            var read = await ReadCoreAsync(destination, ReadBudget, cancellationToken).ConfigureAwait(false);
            return read;
        }).ConfigureAwait(false);
    }
    public async ValueTask<WalSnapshot> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrEmpty(snapshotPath);
        return await AdmittedAsync(async () =>
        {

            var lane = _store.WriteLane;
            var snapshot = await _store.WithWritersHeldAsync(async () =>
            {
                var read = await ReadCoreAsync(destination, long.MaxValue, cancellationToken, lane).ConfigureAwait(false);
                await _store.OnWriterAsync(writer =>
                {

                    Reset(writer);
                    return Truncate(writer);
                }, cancellationToken).ConfigureAwait(false);

                await lane.RunAsync(() => Execute("BEGIN;"), cancellationToken).ConfigureAwait(false);
                try
                {
                    await lane.RunAsync(() => Execute("SELECT count(*) FROM sqlite_schema;"), cancellationToken).ConfigureAwait(false);
                }
                catch
                {

                    await ReplaceReaderAsync().ConfigureAwait(false);
                    throw;
                }

                return await lane.RunAsync(() =>
                {
                    using var command = _reader.CreateCommand();
                    command.CommandText = "SELECT incarnation, value FROM commit_offset WHERE id = 1;";
                    using var identity = command.ExecuteReader();
                    if (!identity.Read() || !Ulid.TryParse(identity.GetString(0), out var incarnation) || incarnation == Ulid.Empty)
                    {
                        throw new InvalidDataException("the pinned backup snapshot has no file identity");
                    }

                    return new WalSnapshot(read, _time.GetUtcNow(), incarnation, identity.GetInt64(1));
                }, cancellationToken).ConfigureAwait(false);
            }, cancellationToken, "the continuous backup's snapshot").ConfigureAwait(false);

            try
            {
                var targetConnection = await _store.Encryption.CreateAsync(snapshotPath, _io, cancellationToken).ConfigureAwait(false);
                await _io.RunAsync(() => TursoCopy.Write(_reader, targetConnection, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await EndSnapshotAsync().ConfigureAwait(false);
            }

            return snapshot;
        }).ConfigureAwait(false);
    }

    private async Task EndSnapshotAsync()
    {
        try
        {
            await _io.RunAsync(() => Execute("ROLLBACK;"), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await ReplaceReaderAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReplaceReaderAsync()
    {
        var stale = _reader;

        await _io.RunAsync(stale.Dispose, CancellationToken.None).ConfigureAwait(false);
        _reader = new TursoConnection(await _store.ConnectionStringAsync(_io, CancellationToken.None).ConfigureAwait(false))
        {
            CloseRefusal = () => _store.CloseRefusal ?? "terminal backup reader close",
        };
        await _io.RunAsync(() => TursoSession.OpenExisting(_reader, _store.FilePath), CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _store.BackupAnchor = null;
        await _io.RunAsync(_reader.Dispose, CancellationToken.None).ConfigureAwait(false);
        _store.CloseBackupReader();
        Interlocked.Decrement(ref _store.BackupTaps);
    }

    private async Task<T> AdmittedAsync<T>(Func<Task<T>> call)
    {

        ObjectDisposedException.ThrowIf(_lineage != _store.Lineage, _store);
        _io.Admit();
        try
        {
            return await _io.WithAdmissionAsync(async () =>
            {
                await using var operation = await _store.BeginOperationAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    var result = await call().ConfigureAwait(false);
                    operation?.Complete();
                    return result;
                }
                catch (Exception error)
                {
                    await _store.RecordOperationDamageAsync(error).ConfigureAwait(false);
                    if (_store.CopyFault is null)
                    {
                        await ReplaceReaderAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        await _io.RunAsync(_reader.Dispose, CancellationToken.None).ConfigureAwait(false);
                    }
                    operation?.Complete();
                    throw;
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _io.Release();
        }
    }

    private async Task<WalRead> ReadCoreAsync(Stream destination, long budget, CancellationToken cancellationToken, TursoRunner? runner = null)
    {
        ObjectDisposedException.ThrowIf(_store.Closed, _store);
        runner ??= _io;
        var (end, started) = await runner.RunAsync(() =>
        {

            var now = _time.GetUtcNow();
            if (_anchor > 0 && Scalar("SELECT 1 FROM turso_cdc WHERE change_id = $anchor;", _anchor) is null)
            {
                return (-1L, now);
            }

            return (Scalar("SELECT max(change_id) FROM turso_cdc WHERE change_id > $anchor AND change_type = 2;", _anchor) as long? ?? _anchor, now);
        }, cancellationToken).ConfigureAwait(false);
        if (end < 0)
        {
            return new(0, false, false, null);
        }

        var buffer = new ArrayBufferWriter<byte>();
        var schema = new Dictionary<long, (string Type, string Name)>(_schema);
        long bytes = 0;
        var after = _anchor;
        var spent = false;
        while (after < end && !spent)
        {
            var (next, placed, stop) = await runner.RunAsync(() => ReadPage(after, end, budget - bytes, schema, buffer), cancellationToken).ConfigureAwait(false);
            if (!placed)
            {
                return new(0, false, false, null);
            }

            await destination.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            bytes += buffer.WrittenCount;
            buffer.ResetWrittenCount();
            (after, spent) = (next, stop);
        }

        DateTimeOffset? mark = null;
        if (after == end && bytes > 0)
        {

            ChangeLog.WriteReadMark(buffer, started);
            await destination.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            bytes += buffer.WrittenCount;
            mark = started;
        }

        (_anchor, _schema) = (after, schema);
        return new(bytes, true, after < end, mark);
    }

    private (long Next, bool Placed, bool Spent) ReadPage(
        long after, long end, long allowance, Dictionary<long, (string Type, string Name)> schema, ArrayBufferWriter<byte> buffer)
    {
        using var command = _reader.CreateCommand();
        command.CommandText = "SELECT change_id, change_type, table_name, id, after, change_time FROM turso_cdc WHERE change_id > $after AND change_id <= $end ORDER BY change_id LIMIT $limit;";
        command.Parameters.Add(new TursoParameter("$after", after));
        command.Parameters.Add(new TursoParameter("$end", end));
        command.Parameters.Add(new TursoParameter("$limit", (long)PageRows));

        using var rows = command.ExecuteReader();
        var last = after;
        if (after == _anchor)
        {

            _open = null;
        }

        while (buffer.WrittenCount < PageBytes && rows.Read())
        {
            last = rows.GetInt64(0);
            var type = rows.GetInt64(1);

            long? time = rows.IsDBNull(5) ? null : rows.GetInt64(5);
            _open = _open is not { } open ? (time ?? 0, time ?? 0, time is not null)
                : (open.First, Math.Max(open.Latest, time ?? open.Latest), open.Ordered && time >= open.Latest);
            if (type == 2)
            {

                if (_open is (var first, var latest, true))
                {
                    ChangeLog.WriteCommit(buffer, first, latest);
                }
                else
                {
                    ChangeLog.WriteCommit(buffer);
                }

                _open = null;
                if (buffer.WrittenCount >= allowance)
                {
                    FaultAwareCopySource.Abort(rows);
                    return (last, true, true);
                }

                continue;
            }

            var table = rows.GetString(2);
            var rowid = rows.GetInt64(3);

            var value = rows.GetValue(4);
            var image = value is DBNull ? null : (byte[])value;
            if (table == "sqlite_schema")
            {
                if (!Place(type, rowid, image, schema, buffer))
                {
                    FaultAwareCopySource.Abort(rows);
                    return (last, false, false);
                }
            }
            else if (!ChangeLog.EngineTable(table))
            {
                if (type == -1)
                {
                    ChangeLog.WriteDelete(buffer, table, rowid);
                }
                else
                {
                    ChangeLog.WriteUpsert(buffer, table, rowid, image ?? throw new InvalidDataException("a captured row change has no after-image"));
                }
            }
        }

        if (buffer.WrittenCount >= PageBytes)
        {
            FaultAwareCopySource.Abort(rows);
        }

        return (last, true, false);
    }

    private static bool Place(long type, long rowid, byte[]? image, Dictionary<long, (string Type, string Name)> schema, ArrayBufferWriter<byte> buffer)
    {
        if (type == 1 && image is not null && SqliteRecord.Decode(image) is [string kind, string name, _, _, var sql, ..])
        {
            schema[rowid] = (kind, name);

            if (sql is string statement && !ChangeLog.EngineTable(name))
            {
                ChangeLog.WriteCreate(buffer, statement);
            }

            return true;
        }

        if (type == -1 && schema.Remove(rowid, out var dropped))
        {
            if (!ChangeLog.EngineTable(dropped.Name))
            {
                ChangeLog.WriteDrop(buffer, dropped.Type, dropped.Name);
            }
            return true;
        }

        return false;
    }

    private bool Reset(TursoSession writer) => Reset(writer.Connection);

    private bool Reset(DbConnection connection, bool publish = true)
    {
        using (var newest = connection.CreateCommand())
        {
            newest.CommandText = TursoStatements.NewestChange;
            _anchor = (long)newest.ExecuteScalar()!;
            if (publish)
            {
                _store.BackupAnchor = _durableAnchor;
            }
        }

        var schema = new Dictionary<long, (string Type, string Name)>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid, type, name FROM sqlite_schema;";
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            schema[rows.GetInt64(0)] = (rows.GetString(1), rows.GetString(2));
        }

        _schema = schema;
        return true;
    }

    internal static TursoChangeTap RewriteFrom(TursoDatabaseStore store, TursoIoPool io, TursoConnection source)
    {
        var tap = new TursoChangeTap(store, io, store.Time, source, store.Lineage);
        tap.Reset(source, publish: false);
        store.RewriteAnchor = tap._anchor;
        return tap;
    }

    internal long RewriteBytes { get; private set; }

    internal long ReplayRewrite(TursoConnection source, TursoConnection target, bool final, CancellationToken token)
    {
        _reader = source;
        if (_anchor > 0 && Scalar("SELECT 1 FROM turso_cdc WHERE change_id = $anchor;", _anchor) is null)
        {
            throw new RewriteRefusedException("the rewrite's change-log anchor was trimmed");
        }

        var end = _anchor;
        if (final)
        {
            using var measured = _store.MeasureRewrite("final_preflight");
            using var check = source.CreateCommand();
            check.CommandText = "SELECT change_id, change_type, length(after), table_name FROM turso_cdc WHERE change_id > $after ORDER BY change_id LIMIT 1025;";
            check.Parameters.Add(new TursoParameter("$after", _anchor));
            using var rows = check.ExecuteReader();
            long count = 0, bytes = 0, last = _anchor;
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                last = rows.GetInt64(0);
                bytes += rows.IsDBNull(2) ? 0 : rows.GetInt64(2);
                if (++count > 1024 || bytes > PageBytes || !rows.IsDBNull(3) && rows.GetString(3) == "sqlite_schema")
                {
                    FaultAwareCopySource.Abort(rows);
                    throw new RewriteRefusedException("the final rewrite delta exceeds its budget", reason: new Budget());
                }
                if (rows.GetInt64(1) == 2)
                {
                    end = last;
                }
            }
            if (last != end)
            {
                throw new RewriteRefusedException("the final rewrite delta ends inside a transaction");
            }
        }
        else
        {
            end = Scalar("SELECT max(change_id) FROM turso_cdc WHERE change_type = 2 AND change_id > $anchor;", _anchor) as long? ?? _anchor;
        }

        if (_anchor == 0 && Scalar("SELECT min(change_id) FROM turso_cdc WHERE change_id > $anchor;", 0) as long? is > 1)
        {
            throw new RewriteRefusedException("the rewrite's initial change-log prefix was trimmed");
        }

        var first = _anchor;
        RewriteBytes = 0;
        var replay = new ChangeLog.Replay(target);
        replay.Suspend();
        var buffer = new ArrayBufferWriter<byte>();
        while (_anchor < end)
        {
            token.ThrowIfCancellationRequested();
            var (next, placed, _) = ReadPage(_anchor, end, long.MaxValue, _schema, buffer);
            RewriteBytes += buffer.WrittenCount;
            using var segment = new MemoryStream(buffer.WrittenMemory.ToArray(), writable: false);
            if (!placed || next <= _anchor || !replay.Apply(segment, token))
            {
                throw new RewriteRefusedException("the rewrite delta cannot be replayed");
            }

            _anchor = next;
            buffer.ResetWrittenCount();
        }

        if (replay.Open)
        {
            throw new RewriteRefusedException("the rewrite delta ends inside a transaction");
        }

        replay.Resume();
        _store.RewriteAnchor = _anchor;
        return _anchor - first;
    }

    private bool Truncate(TursoSession writer)
    {
        var rewrite = _store.RewriteAnchor;
        var below = rewrite < 0 ? _durableAnchor : Math.Min(_durableAnchor, rewrite);
        _ = TursoStorageTransaction.Trim(writer.Connection, null, below, _store.IsSystem);
        _store.ObserveCdcSize(writer.Connection);
        return true;
    }

    private void Execute(params string[] statements)
    {
        foreach (var sql in statements)
        {
            using var command = _reader.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private object? Scalar(string sql, long anchor)
    {
        using var command = _reader.CreateCommand();
        command.CommandText = sql;
        if (sql.Contains("$anchor", StringComparison.Ordinal))
        {
            command.Parameters.Add(new TursoParameter("$anchor", anchor));
        }
        return command.ExecuteScalar();
    }
}
