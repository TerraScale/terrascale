using System.Buffers;
using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Turso;
using Turso.Data.Sqlite;
using Turso.Raw.Public;

namespace TerraBase.Engine.Turso;

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
            await io.RunAsync(replay.Suspend, cancellationToken).ConfigureAwait(false);
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

            await io.RunAsync(replay.Resume, cancellationToken).ConfigureAwait(false);
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
                        SkipPayload(segment, kind);
                        break;
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
                if (kind == ReadMark)
                {
                    mark = DateTimeOffset.FromUnixTimeMilliseconds(ReadInt64(segment));
                }
                else
                {
                    SkipPayload(segment, kind);
                }
            }

            return mark;
        }
        catch (Exception error) when (error is EndOfStreamException or DecoderFallbackException)
        {
            throw new InvalidDataException("a change log is malformed", error);
        }
    }

    private static void SkipPayload(Stream segment, int kind)
    {
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
                ReadInt64(segment);
                break;
            default:
                throw new InvalidDataException("a change log record has an unknown kind");
        }
    }

    internal sealed class Replay(TursoConnection connection)
    {
        private readonly Dictionary<string, (string[] Columns, int Alias)> _tables = [with(StringComparer.Ordinal)];
        private readonly List<(string Name, string Table, string Sql)> _triggers = [];
        private long? _foreignKeys;

        public bool Open { get; private set; }

        public void Suspend()
        {
            _foreignKeys ??= Convert.ToInt64(Query("PRAGMA foreign_keys;").Single()[0], CultureInfo.InvariantCulture);
            Execute("PRAGMA foreign_keys = OFF;");
            StashTriggers();
        }

        public void Resume()
        {
            foreach (var (_, _, sql) in _triggers)
            {
                Execute(sql);
            }

            _triggers.Clear();
            if (_foreignKeys is { } enforced)
            {
                Execute(string.Create(CultureInfo.InvariantCulture, $"PRAGMA foreign_keys = {enforced};"));
                _foreignKeys = null;
            }
        }

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
                            if (_foreignKeys is not null)
                            {
                                StashTriggers();
                            }

                            break;
                        case Drop:
                            var type = ReadString(segment);
                            var name = ReadString(segment);
                            if (type is not ("table" or "index" or "view" or "trigger"))
                            {
                                return false;
                            }

                            Execute($"DROP {type.ToUpperInvariant()} IF EXISTS {TableShape.Quote(name)};");
                            if (type != "index")
                            {
                                _triggers.RemoveAll(trigger => string.Equals(type == "trigger" ? trigger.Name : trigger.Table, name, StringComparison.OrdinalIgnoreCase));
                            }

                            _tables.Clear();
                            break;
                        default:
                            return false;
                    }
                }

                return true;
            }
            catch (Exception error) when (error is TursoException or SqliteException or InvalidDataException or EndOfStreamException
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

        private void StashTriggers()
        {
            foreach (var row in Query("SELECT name, tbl_name, sql FROM sqlite_schema WHERE type = 'trigger' AND sql IS NOT NULL ORDER BY rowid;"))
            {
                var trigger = ((string)row[0]!, (string)row[1]!, (string)row[2]!);
                Execute($"DROP TRIGGER {TableShape.Quote(trigger.Item1)};");
                _triggers.Add(trigger);
            }
        }

        private List<object?[]> Query(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read())
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return rows;
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
