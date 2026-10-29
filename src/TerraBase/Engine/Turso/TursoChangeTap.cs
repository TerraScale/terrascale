using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Turso;

namespace TerraBase.Engine.Turso;

internal sealed class TursoChangeTap : IWalTap
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

    private Dictionary<long, (string Type, string Name)> _schema = [];

    private (long First, long Latest, bool Ordered)? _open;

    internal Func<CancellationToken, Task>? AfterSnapshotPinned { get; set; }

    internal Func<CancellationToken, Task>? BeforeTrim { get; set; }

    internal Func<CancellationToken, Task>? BeforeSnapshotEnd { get; set; }

    internal Func<CancellationToken, Task>? AfterSnapshotBegin { get; set; }

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
                return tap.Reset(writer);
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

    public bool Ended => _lineage != _store.Lineage;

    public async ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return await AdmittedAsync(async () =>
        {
            var (anchor, schema) = (_anchor, _schema);
            var read = await ReadCoreAsync(destination, ReadBudget, cancellationToken).ConfigureAwait(false);
            if (!read.Continuous)
            {
                await _store.WithWriterAsync(Reset, "the continuous backup's tap", cancellationToken).ConfigureAwait(false);
            }
            else if (read.Bytes > 0)
            {
                try
                {
                    if (BeforeTrim is { } trim)
                    {
                        await trim(cancellationToken).ConfigureAwait(false);
                    }

                    await _store.WithWriterAsync(Truncate, "the continuous backup's cleanup", cancellationToken).ConfigureAwait(false);
                }
                catch
                {

                    (_anchor, _schema) = (anchor, schema);
                    throw;
                }

                _store.BackupAnchor = _anchor;
            }

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
                    if (AfterSnapshotBegin is { } begun)
                    {
                        await begun(cancellationToken).ConfigureAwait(false);
                    }

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
                if (AfterSnapshotPinned is { } pinned)
                {
                    await pinned(cancellationToken).ConfigureAwait(false);
                }

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
            if (BeforeSnapshotEnd is { } ending)
            {
                await ending(CancellationToken.None).ConfigureAwait(false);
            }

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

    private bool Reset(System.Data.Common.DbConnection connection, bool publish = true)
    {
        using (var newest = connection.CreateCommand())
        {
            newest.CommandText = TursoStatements.NewestChange;
            _anchor = (long)newest.ExecuteScalar()!;
            if (publish)
            {
                _store.BackupAnchor = _anchor;
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
        var tap = new TursoChangeTap(store, io, TimeProvider.System, source, store.Lineage);
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
                    throw new RewriteRefusedException("the final rewrite delta exceeds its budget", reason: RewriteRefusal.Budget);
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

        _store.RewriteAnchor = _anchor;
        return _anchor - first;
    }

    private bool Truncate(TursoSession writer)
    {
        var rewrite = _store.RewriteAnchor;
        var below = rewrite < 0 ? _anchor : Math.Min(_anchor, rewrite);
        _ = TursoStorageTransaction.Trim(writer.Connection, null, below);
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
        command.Parameters.Add(new TursoParameter("$anchor", anchor));
        return command.ExecuteScalar();
    }
}

internal static class ChangeLog
{
    private const byte Upsert = 1;
    private const byte Delete = 2;
    private const byte Commit = 3;
    private const byte Create = 4;
    private const byte Drop = 5;
    private const byte TimedCommit = 6;
    private const byte ReadMark = 7;

    private const int MaxLength = 1 << 30;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool EngineTable(string table) =>
    table.StartsWith("sqlite_", StringComparison.Ordinal) || table.StartsWith("turso_", StringComparison.Ordinal)
    || table.StartsWith("__turso_", StringComparison.Ordinal)
    || table.StartsWith("fts_dir__tb_fts_index_", StringComparison.Ordinal);

    public static void WriteUpsert(IBufferWriter<byte> buffer, string table, long rowid, byte[] record)
    {
        buffer.Write([Upsert]);
        WriteString(buffer, table);
        WriteInt64(buffer, rowid);
        WriteLength(buffer, record.Length);
        buffer.Write(record);
    }

    public static void WriteDelete(IBufferWriter<byte> buffer, string table, long rowid)
    {
        buffer.Write([Delete]);
        WriteString(buffer, table);
        WriteInt64(buffer, rowid);
    }

    public static void WriteCommit(IBufferWriter<byte> buffer) => buffer.Write([Commit]);

    public static void WriteCommit(IBufferWriter<byte> buffer, long first, long last)
    {
        buffer.Write([TimedCommit]);
        WriteInt64(buffer, first);
        WriteInt64(buffer, last);
    }

    public static void WriteReadMark(IBufferWriter<byte> buffer, DateTimeOffset started)
    {
        buffer.Write([ReadMark]);
        WriteInt64(buffer, started.ToUnixTimeMilliseconds());
    }

    public static void WriteCreate(IBufferWriter<byte> buffer, string sql)
    {
        buffer.Write([Create]);
        WriteString(buffer, sql);
    }

    public static void WriteDrop(IBufferWriter<byte> buffer, string type, string name)
    {
        buffer.Write([Drop]);
        WriteString(buffer, type);
        WriteString(buffer, name);
    }

    public static async Task<bool> ApplyAsync(string file, IAsyncEnumerable<Stream> segments, TursoRunner io, string connectionString, CancellationToken cancellationToken)
    {
        var connection = new TursoConnection(connectionString);
        try
        {
            await io.RunAsync(() => TursoSession.OpenExisting(connection, file), cancellationToken).ConfigureAwait(false);
            var replay = new Replay(connection);
            await foreach (var segment in segments.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await using (segment.ConfigureAwait(false))
                {
                    if (!await io.RunAsync(() => replay.Apply(segment, cancellationToken), cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                }
            }

            if (replay.Open)
            {
                return false;
            }

            await io.RunAsync(replay.Checkpoint, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            await io.RunAsync(connection.Dispose, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public static LogCut Cut(Stream segment, DateTimeOffset at, long? previous)
    {
        var time = at.ToUnixTimeMilliseconds();
        long before = 0;
        var reached = false;
        var open = false;
        try
        {
            int kind;
            while ((kind = segment.ReadByte()) >= 0)
            {
                open = kind is Upsert or Delete or Create or Drop;
                switch (kind)
                {
                    case Upsert:
                        ReadString(segment);
                        ReadInt64(segment);
                        ReadBytes(segment);
                        break;
                    case Delete:
                        ReadString(segment);
                        ReadInt64(segment);
                        break;
                    case Create:
                        ReadString(segment);
                        break;
                    case Drop:
                        ReadString(segment);
                        ReadString(segment);
                        break;
                    case Commit when !reached:
                        return new(before, false, previous, "a transaction it needs has no recorded time (its log predates them, or the clock went back within it)");
                    case Commit:
                        break;
                    case TimedCommit:
                        var (first, last) = (ReadInt64(segment), ReadInt64(segment));
                        if (first < previous)
                        {
                            return new(before, false, previous, "the database's clock went back between two of its transactions");
                        }

                        previous = last;
                        if (reached || last * 1000 >= time)
                        {
                            reached = true;
                        }
                        else if ((last + 1) * 1000 <= time)
                        {
                            before = segment.Position;
                        }
                        else
                        {
                            var second = DateTimeOffset.FromUnixTimeSeconds(last);
                            return new(before, false, previous, string.Create(CultureInfo.InvariantCulture,
                                $"one committed in its second, {second:yyyy-MM-ddTHH:mm:ssZ} to {second.AddSeconds(1):yyyy-MM-ddTHH:mm:ssZ}, and transaction times are whole seconds; the nearest earlier time it can place is {second:yyyy-MM-ddTHH:mm:ssZ}"));
                        }

                        break;
                    case ReadMark:
                        reached |= ReadInt64(segment) >= time;
                        before = reached ? before : segment.Position;
                        break;
                    default:
                        throw new InvalidDataException("a change log record has an unknown kind");
                }
            }

            return open ? throw new InvalidDataException("a change log ends inside a transaction") : new(before, reached, previous, null);
        }
        catch (Exception error) when (error is EndOfStreamException or DecoderFallbackException)
        {
            throw new InvalidDataException("a change log is malformed", error);
        }
    }

    public static DateTimeOffset? EndMark(Stream segment)
    {
        DateTimeOffset? mark = null;
        try
        {
            int kind;
            while ((kind = segment.ReadByte()) >= 0)
            {
                mark = null;
                switch (kind)
                {
                    case Upsert:
                        ReadString(segment);
                        ReadInt64(segment);
                        ReadBytes(segment);
                        break;
                    case Delete:
                        ReadString(segment);
                        ReadInt64(segment);
                        break;
                    case Create:
                        ReadString(segment);
                        break;
                    case Drop:
                        ReadString(segment);
                        ReadString(segment);
                        break;
                    case Commit:
                        break;
                    case TimedCommit:
                        ReadInt64(segment);
                        ReadInt64(segment);
                        break;
                    case ReadMark:
                        mark = DateTimeOffset.FromUnixTimeMilliseconds(ReadInt64(segment));
                        break;
                    default:
                        throw new InvalidDataException("a change log record has an unknown kind");
                }
            }

            return mark;
        }
        catch (Exception error) when (error is EndOfStreamException or DecoderFallbackException)
        {
            throw new InvalidDataException("a change log is malformed", error);
        }
    }

    internal sealed class Replay(TursoConnection connection)
    {

        private readonly Dictionary<string, (string[] Columns, int Alias)> _tables = [with(StringComparer.Ordinal)];

        public bool Open { get; private set; }

        public bool Apply(Stream segment, CancellationToken cancellationToken)
        {
            try
            {
                int kind;
                while ((kind = segment.ReadByte()) >= 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (kind is Commit or TimedCommit)
                    {
                        if (kind == TimedCommit)
                        {
                            ReadInt64(segment);
                            ReadInt64(segment);
                        }

                        if (Open)
                        {
                            Execute("COMMIT;");
                            Open = false;
                        }

                        continue;
                    }

                    if (kind == ReadMark)
                    {
                        ReadInt64(segment);
                        continue;
                    }

                    if (!Open)
                    {
                        Execute("BEGIN IMMEDIATE;");
                        Open = true;
                    }

                    switch (kind)
                    {
                        case Upsert:
                            ApplyUpsert(ReadString(segment), ReadInt64(segment), ReadBytes(segment));
                            break;
                        case Delete:
                            Execute($"DELETE FROM {TableShape.Quote(ReadString(segment))} WHERE rowid = $rowid;", ("$rowid", ReadInt64(segment)));
                            break;
                        case Create:
                            Execute(ReadString(segment));
                            _tables.Clear();
                            break;
                        case Drop:
                            var type = ReadString(segment);
                            var name = ReadString(segment);
                            if (type is not ("table" or "index" or "view" or "trigger"))
                            {
                                return false;
                            }

                            Execute($"DROP {type.ToUpperInvariant()} IF EXISTS {TableShape.Quote(name)};");
                            _tables.Clear();
                            break;
                        default:
                            return false;
                    }
                }

                return true;
            }
            catch (Exception error) when (error is global::Turso.Raw.Public.TursoException or global::Turso.Data.Sqlite.SqliteException or InvalidDataException or EndOfStreamException
                or DecoderFallbackException)
            {
                return false;
            }
        }

        public void Checkpoint() => Execute("PRAGMA wal_checkpoint(TRUNCATE);");

        private void ApplyUpsert(string table, long rowid, byte[] record)
        {
            var values = SqliteRecord.Decode(record);
            var (columns, alias) = Shape(table);
            if (values.Length > columns.Length)
            {
                throw new InvalidDataException("a logged row has more values than its table has columns");
            }

            var names = new List<string> { "rowid" };
            var parameters = new List<(string, object?)> { ("$rowid", rowid) };
            for (var i = 0; i < values.Length; i++)
            {

                if (i != alias)
                {
                    names.Add(TableShape.Quote(columns[i]));
                    parameters.Add(($"$v{i}", values[i]));
                }
            }

            Execute($"INSERT OR REPLACE INTO {TableShape.Quote(table)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", parameters.Select(p => p.Item1))});",
                [.. parameters]);
        }

        private (string[] Columns, int Alias) Shape(string table)
        {
            if (!_tables.TryGetValue(table, out var shape))
            {
                shape = _tables[table] = TableShape.Read(connection, table);
            }

            return shape;
        }

        private void Execute(string sql, params (string Name, object? Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.Add(new TursoParameter(name, value ?? DBNull.Value));
            }

            command.ExecuteNonQuery();
        }

    }

    private static void WriteString(IBufferWriter<byte> buffer, string value)
    {
        var length = StrictUtf8.GetByteCount(value);
        WriteLength(buffer, length);
        buffer.Advance(StrictUtf8.GetBytes(value, buffer.GetSpan(length)));
    }

    private static void WriteInt64(IBufferWriter<byte> buffer, long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(buffer.GetSpan(8), value);
        buffer.Advance(8);
    }

    private static void WriteLength(IBufferWriter<byte> buffer, int length)
    {
        var value = (uint)length;
        while (value >= 0x80)
        {
            buffer.Write([(byte)(value | 0x80)]);
            value >>= 7;
        }

        buffer.Write([(byte)value]);
    }

    private static string ReadString(Stream segment) => StrictUtf8.GetString(ReadBytes(segment));

    private static long ReadInt64(Stream segment)
    {
        Span<byte> bytes = stackalloc byte[8];
        segment.ReadExactly(bytes);
        return BinaryPrimitives.ReadInt64BigEndian(bytes);
    }

    private static byte[] ReadBytes(Stream segment)
    {
        var bytes = new byte[ReadLength(segment)];
        segment.ReadExactly(bytes);
        return bytes;
    }

    private static int ReadLength(Stream segment)
    {
        long value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var next = segment.ReadByte();
            if (next < 0)
            {
                throw new EndOfStreamException("a change log record is truncated");
            }

            value |= (long)(next & 0x7f) << shift;
            if ((next & 0x80) == 0)
            {
                return value <= MaxLength ? (int)value : throw new InvalidDataException("a change log length is out of range");
            }
        }

        throw new InvalidDataException("a change log length is malformed");
    }
}

internal static class SqliteRecord
{

    public static object?[] Decode(ReadOnlySpan<byte> record, int columns = int.MaxValue, bool materializeContent = true)
    {
        var headerSize = Varint(record, out var used);
        if (headerSize < used || headerSize > record.Length)
        {
            throw new InvalidDataException("a record header is out of range");
        }

        var header = record[used..(int)headerSize];
        var body = record[(int)headerSize..];
        var values = new List<object?>();
        while (!header.IsEmpty && values.Count < columns)
        {
            var type = Varint(header, out var read);
            header = header[read..];
            var size = type switch
            {
                0 or 8 or 9 => 0,
                1 => 1,
                2 => 2,
                3 => 3,
                4 => 4,
                5 => 6,
                6 or 7 => 8,
                >= 12 => (int)((type - 12) / 2),
                _ => throw new InvalidDataException("a record uses a reserved serial type"),
            };
            if (size > body.Length)
            {
                throw new InvalidDataException("a record value runs past its end");
            }

            var value = body[..size];
            body = body[size..];

            values.Add(type switch
            {
                0 => null,
                8 => 0L,
                9 => 1L,
                7 => BinaryPrimitives.ReadDoubleBigEndian(value),
                <= 6 => Integer(value),
                _ when type % 2 == 0 => materializeContent ? value.ToArray() : [],
                _ => materializeContent ? Encoding.UTF8.GetString(value) : string.Empty,
            });
        }

        return [.. values];
    }

    private static long Integer(ReadOnlySpan<byte> bytes)
    {
        long value = (sbyte)bytes[0];
        foreach (var next in bytes[1..])
        {
            value = (value << 8) | next;
        }

        return value;
    }

    private static long Varint(ReadOnlySpan<byte> bytes, out int read)
    {
        long value = 0;
        for (read = 0; read < 9; read++)
        {
            if (read >= bytes.Length)
            {
                throw new InvalidDataException("a record varint is truncated");
            }

            var next = bytes[read];
            if (read == 8)
            {
                read++;
                return (value << 8) | next;
            }

            value = (value << 7) | (long)(next & 0x7f);
            if ((next & 0x80) == 0)
            {
                read++;
                return value;
            }
        }

        throw new InvalidDataException("a record varint is malformed");
    }
}
