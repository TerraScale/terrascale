using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Turso;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// The log continuous backup ships for one database file (TS-633, <c>docs/terrabase/backup.md</c>): the changes Turso
/// captured in <c>turso_cdc</c> on the file's writer connection (mode <c>after</c>), in commit order. Turso checkpoints
/// its write-ahead log on its own (it ignores <c>wal_autocheckpoint</c>), so its log file cannot be shipped as SQLite's
/// was; the captured changes are what every commit adds instead.
/// </summary>
/// <remarks>
/// <para>
/// The tap reads rows after the last one it read (its anchor) on its own <see cref="TursoConnection"/>, up to the last
/// commit row or the first commit past <see cref="ReadBudget"/>, and appends them as <see cref="ChangeLog"/> records. It
/// then deletes the rows before its anchor on the writer, always keeping the newest: an empty <c>turso_cdc</c> restarts
/// its ids at 1 after a reopen. An anchor that is gone means someone else deleted rows the tap never read: a hole, so the
/// backup starts a new generation.
/// </para>
/// <para>
/// A delete in mode <c>after</c> carries only the rowid. For <c>sqlite_schema</c> that is not enough, because Turso does
/// not capture every schema row (<c>ANALYZE</c> adds <c>sqlite_stat1</c> uncaptured), so rowids differ between the file
/// and a restore of it. The tap keeps the live schema's rowid → name map, as of its anchor, and logs a dropped object by
/// name. A schema row it cannot place (an update, an unknown rowid) is a hole too.
/// </para>
/// <para>
/// A snapshot holds the writer, so nothing commits, while it reads what is left and opens a read transaction. Then writers
/// go on, and <see cref="TursoCopy"/> copies the database as of that transaction: exactly as of the last record appended.
/// </para>
/// </remarks>
internal sealed class TursoChangeTap : IWalTap
{
    // Rows read per call, and bytes appended per call before the rest of the page is left for the next one.
    private const int PageRows = 1024;
    private const int PageBytes = 4 << 20;

    // Log bytes one ReadAsync appends before it stops at the next commit and leaves the rest to the next call: a segment
    // holds about this much, and a backlog (after a long snapshot upload) ships as several segments at upload speed.
    private const long ReadBudget = 32L << 20;

    private readonly SqliteDatabaseStore _store;
    private readonly SqliteIoPool _io;
    private readonly TimeProvider _time;
    private readonly TursoConnection _reader;

    // The change_id of the last row read, kept in turso_cdc; 0 before anything was captured.
    private long _anchor;

    // The file's sqlite_schema as of _anchor: rowid -> (type, name).
    private Dictionary<long, (string Type, string Name)> _schema = [];

    /// <summary>Runs after a snapshot is pinned and writers are released, before the file is copied; for integration tests.</summary>
    internal Func<CancellationToken, Task>? AfterSnapshotPinned { get; set; }

    private TursoChangeTap(SqliteDatabaseStore store, SqliteIoPool io, TimeProvider time, TursoConnection reader)
    {
        _store = store;
        _io = io;
        _time = time;
        _reader = reader;
    }

    /// <summary>Opens a tap on <paramref name="store"/> that starts at the newest change; the caller holds an admission.</summary>
    public static async Task<TursoChangeTap> OpenAsync(SqliteDatabaseStore store, SqliteIoPool io, TimeProvider time, CancellationToken cancellationToken)
    {
        var reader = new TursoConnection($"Data Source={store.FilePath}");
        try
        {
            await io.RunAsync(() => SqliteSession.OpenExisting(reader, store.FilePath), cancellationToken).ConfigureAwait(false);
            var tap = new TursoChangeTap(store, io, time, reader);
            // Under the writer, so the anchor and the schema map describe the same commit; this also opens the writer, whose
            // capture creates turso_cdc.
            await store.WithWriterAsync(tap.Reset, "the continuous backup's tap", cancellationToken).ConfigureAwait(false);
            return tap;
        }
        catch
        {
            await io.RunAsync(reader.Dispose, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

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
                    await _store.WithWriterAsync(Truncate, "the continuous backup's cleanup", cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // A call that throws has not moved the tap (IWalTap.ReadAsync): the next one reads the same changes
                    // again, or finds a hole if the delete ran.
                    (_anchor, _schema) = (anchor, schema);
                    _store.BackupAnchor = anchor;
                    throw;
                }
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
            var snapshot = await _store.WithWritersHeldAsync(async () =>
            {
                var read = await ReadCoreAsync(destination, long.MaxValue, cancellationToken).ConfigureAwait(false);
                await _store.OnWriterAsync(writer =>
                {
                    // Nothing commits meanwhile: the anchor is the newest row, and after a hole the next generation starts here.
                    Reset(writer);
                    return Truncate(writer);
                }, cancellationToken).ConfigureAwait(false);
                // The read transaction the copy is made in, pinned at the anchor.
                await _io.RunAsync(() => Execute("BEGIN;", "SELECT count(*) FROM sqlite_schema;"), cancellationToken).ConfigureAwait(false);
                return new WalSnapshot(read, _time.GetUtcNow());
            }, cancellationToken, "the continuous backup's snapshot").ConfigureAwait(false);

            try
            {
                if (AfterSnapshotPinned is { } pinned)
                {
                    await pinned(cancellationToken).ConfigureAwait(false);
                }

                await _io.RunAsync(() => TursoCopy.Write(_reader, snapshotPath, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await _io.RunAsync(() => Execute("ROLLBACK;"), CancellationToken.None).ConfigureAwait(false);
            }

            return snapshot;
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _store.BackupAnchor = null;
        await _io.RunAsync(_reader.Dispose, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<T> AdmittedAsync<T>(Func<Task<T>> call)
    {
        _io.Admit();
        try
        {
            return await call().ConfigureAwait(false);
        }
        finally
        {
            _io.Release();
        }
    }

    // Appends the records committed after the anchor, up to the first commit past `budget` bytes, and moves the anchor to
    // the last of them.
    private async Task<WalRead> ReadCoreAsync(Stream destination, long budget, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_store.Closed, _store);
        var end = await _io.RunAsync(() =>
        {
            if (_anchor > 0 && Scalar("SELECT 1 FROM turso_cdc WHERE change_id = $anchor;", _anchor) is null)
            {
                return -1;
            }

            return Scalar("SELECT max(change_id) FROM turso_cdc WHERE change_id > $anchor AND change_type = 2;", _anchor) as long? ?? _anchor;
        }, cancellationToken).ConfigureAwait(false);
        if (end < 0)
        {
            return new(0, false, false);
        }

        var buffer = new ArrayBufferWriter<byte>();
        var schema = new Dictionary<long, (string Type, string Name)>(_schema);
        long bytes = 0;
        var after = _anchor;
        var spent = false;
        while (after < end && !spent)
        {
            var (next, placed, stop) = await _io.RunAsync(() => ReadPage(after, end, budget - bytes, schema, buffer), cancellationToken).ConfigureAwait(false);
            if (!placed)
            {
                return new(0, false, false);
            }

            await destination.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            bytes += buffer.WrittenCount;
            buffer.ResetWrittenCount();
            (after, spent) = (next, stop);
        }

        (_anchor, _schema) = (after, schema);
        _store.BackupAnchor = after;
        return new(bytes, true, after < end);
    }

    // Appends rows after `after`, up to `end`, and returns the last one read; Placed is false when a schema row cannot be
    // placed, and Spent true when the page stopped at the first commit that reached `allowance` bytes.
    private (long Next, bool Placed, bool Spent) ReadPage(
        long after, long end, long allowance, Dictionary<long, (string Type, string Name)> schema, ArrayBufferWriter<byte> buffer)
    {
        using var command = _reader.CreateCommand();
        command.CommandText = "SELECT change_id, change_type, table_name, id, after FROM turso_cdc WHERE change_id > $after AND change_id <= $end ORDER BY change_id LIMIT $limit;";
        command.Parameters.Add(new TursoParameter("$after", after));
        command.Parameters.Add(new TursoParameter("$end", end));
        command.Parameters.Add(new TursoParameter("$limit", (long)PageRows));
        using var rows = command.ExecuteReader();
        var last = after;
        while (buffer.WrittenCount < PageBytes && rows.Read())
        {
            last = rows.GetInt64(0);
            var type = rows.GetInt64(1);
            if (type == 2)
            {
                ChangeLog.WriteCommit(buffer);
                if (buffer.WrittenCount >= allowance)
                {
                    return (last, true, true);
                }

                continue;
            }

            var table = rows.GetString(2);
            var rowid = rows.GetInt64(3);
            var image = rows.IsDBNull(4) ? null : (byte[])rows.GetValue(4);
            if (table == "sqlite_schema")
            {
                if (!Place(type, rowid, image, schema, buffer))
                {
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

        return (last, true, false);
    }

    // Logs a schema change by name: a created object with its SQL, a dropped one by type and name.
    private static bool Place(long type, long rowid, byte[]? image, Dictionary<long, (string Type, string Name)> schema, ArrayBufferWriter<byte> buffer)
    {
        if (type == 1 && image is not null && SqliteRecord.Decode(image) is [string kind, string name, _, _, var sql, ..])
        {
            schema[rowid] = (kind, name);
            if (sql is string statement)
            {
                ChangeLog.WriteCreate(buffer, statement);
            }

            return true;
        }

        if (type == -1 && schema.Remove(rowid, out var dropped))
        {
            ChangeLog.WriteDrop(buffer, dropped.Type, dropped.Name);
            return true;
        }

        // An update (ALTER), or a row the map does not know.
        return false;
    }

    // On the writer, with the writer held: the anchor becomes the newest change and the schema map the file's.
    private bool Reset(SqliteSession writer)
    {
        using (var newest = writer.Connection.CreateCommand())
        {
            newest.CommandText = "SELECT coalesce(max(change_id), 0) FROM turso_cdc;";
            _anchor = (long)newest.ExecuteScalar()!;
            _store.BackupAnchor = _anchor;
        }

        var schema = new Dictionary<long, (string Type, string Name)>();
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT rowid, type, name FROM sqlite_schema;";
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            schema[rows.GetInt64(0)] = (rows.GetString(1), rows.GetString(2));
        }

        _schema = schema;
        return true;
    }

    // Deletes the rows before the anchor; a client database's peer streams also hold the rows they have not acknowledged
    // (SqliteStorageTransaction.Retention). Turso does not capture changes to turso_cdc itself.
    private bool Truncate(SqliteSession writer)
    {
        using var command = writer.Connection.CreateCommand();
        if (_store.IsSystem)
        {
            command.CommandText = "DELETE FROM turso_cdc WHERE change_id < $anchor;";
            command.Parameters.AddWithValue("$anchor", _anchor);
        }
        else
        {
            command.CommandText = SqliteStorageTransaction.Retention;
            command.Parameters.AddWithValue("$below", _anchor);
            command.Parameters.AddWithValue("$now", _time.GetUtcNow().ToUnixTimeSeconds());
        }

        command.ExecuteNonQuery();
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

/// <summary>
/// The records of a change log segment, format 1. Each starts with its kind: <c>1</c> upsert (table, rowid, the row's
/// SQLite record), <c>2</c> delete (table, rowid), <c>3</c> commit, <c>4</c> create (the object's SQL), <c>5</c> drop (type,
/// name). Strings and records are length-prefixed (unsigned LEB128); rowids are 8 bytes, big-endian.
/// </summary>
internal static class ChangeLog
{
    private const byte Upsert = 1;
    private const byte Delete = 2;
    private const byte Commit = 3;
    private const byte Create = 4;
    private const byte Drop = 5;

    // Bounds a single length field: no record, name or statement of a valid log is near this.
    private const int MaxLength = 1 << 30;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Tables the engine maintains itself: their changes are not logged and a restore never writes them.</summary>
    public static bool EngineTable(string table) =>
        table.StartsWith("sqlite_", StringComparison.Ordinal) || table.StartsWith("turso_", StringComparison.Ordinal)
        || table.StartsWith("__turso_", StringComparison.Ordinal);

    public static void WriteUpsert(IBufferWriter<byte> buffer, string table, long rowid, byte[] record)
    {
        buffer.Write([Upsert]);
        WriteString(buffer, table);
        WriteRowid(buffer, rowid);
        WriteLength(buffer, record.Length);
        buffer.Write(record);
    }

    public static void WriteDelete(IBufferWriter<byte> buffer, string table, long rowid)
    {
        buffer.Write([Delete]);
        WriteString(buffer, table);
        WriteRowid(buffer, rowid);
    }

    public static void WriteCommit(IBufferWriter<byte> buffer) => buffer.Write([Commit]);

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

    /// <summary>
    /// Replays <paramref name="segments"/> on <paramref name="file"/>, a snapshot, one transaction per commit record. Returns
    /// <see langword="false"/> for a malformed log, one that ends inside a transaction, or a change the file refuses.
    /// </summary>
    public static async Task<bool> ApplyAsync(string file, IAsyncEnumerable<Stream> segments, SqliteRunner io, CancellationToken cancellationToken)
    {
        var connection = new TursoConnection($"Data Source={file}");
        try
        {
            await io.RunAsync(connection.Open, cancellationToken).ConfigureAwait(false);
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

            // Turso leaves the log next to the file when the connection closes: copy it in, so the file alone is the result.
            await io.RunAsync(replay.Checkpoint, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            await io.RunAsync(connection.Dispose, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class Replay(TursoConnection connection)
    {
        // Per table: its columns in order and the ordinal of its rowid alias (INTEGER PRIMARY KEY), or -1.
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
                    if (kind == Commit)
                    {
                        if (Open)
                        {
                            Execute("COMMIT;");
                            Open = false;
                        }

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
                            ApplyUpsert(ReadString(segment), ReadRowid(segment), ReadBytes(segment));
                            break;
                        case Delete:
                            Execute($"DELETE FROM {TableShape.Quote(ReadString(segment))} WHERE rowid = $rowid;", ("$rowid", ReadRowid(segment)));
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

                            // A table's indexes go with it, so a later drop of one may find nothing.
                            Execute($"DROP {type.ToUpperInvariant()} IF EXISTS {TableShape.Quote(name)};");
                            _tables.Clear();
                            break;
                        default:
                            return false;
                    }
                }

                return true;
            }
            catch (Exception error) when (error is Turso.Raw.Public.TursoException or Turso.Data.Sqlite.SqliteException or InvalidDataException or EndOfStreamException
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
                // The record stores NULL for the rowid alias; the rowid fills it.
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
        var bytes = StrictUtf8.GetBytes(value);
        WriteLength(buffer, bytes.Length);
        buffer.Write(bytes);
    }

    private static void WriteRowid(IBufferWriter<byte> buffer, long rowid)
    {
        BinaryPrimitives.WriteInt64BigEndian(buffer.GetSpan(8), rowid);
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

    private static long ReadRowid(Stream segment)
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

/// <summary>Decodes a row in SQLite's record format (<c>https://sqlite.org/fileformat.html#record_format</c>).</summary>
internal static class SqliteRecord
{
    public static object?[] Decode(ReadOnlySpan<byte> record)
    {
        var headerSize = Varint(record, out var used);
        if (headerSize < used || headerSize > record.Length)
        {
            throw new InvalidDataException("a record header is out of range");
        }

        var header = record[used..(int)headerSize];
        var body = record[(int)headerSize..];
        var values = new List<object?>();
        while (!header.IsEmpty)
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
                _ when type % 2 == 0 => value.ToArray(),
                _ => Encoding.UTF8.GetString(value),
            });
        }

        return [.. values];
    }

    // A big-endian two's-complement integer of 1 to 8 bytes.
    private static long Integer(ReadOnlySpan<byte> bytes)
    {
        long value = (sbyte)bytes[0];
        foreach (var next in bytes[1..])
        {
            value = (value << 8) | next;
        }

        return value;
    }

    // SQLite's varint: big-endian groups of 7 bits, high bit set on all but the last; a ninth byte carries 8 bits.
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
