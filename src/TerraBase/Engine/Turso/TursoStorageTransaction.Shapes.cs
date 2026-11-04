using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Shapes;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction : IShapeStorageTransaction, IShapeTableStorage
{
    private const int ShapeRows = 4096;
    private const int ShapeScanRows = 65_536;
    private const int ShapeScanBatch = 1024;
    private const int ShapeBodyBytes = 1024 * 1024;
    private const int ShapeCommitBytes = 8 * 1024 * 1024;
    private const string SchemaTable = "sqlite_schema";
    private const string ShapeTablePrefix = "~table:";

    public ValueTask<ShapeTableState> ReadShapeTableAsync(string table, CancellationToken cancellationToken) =>
        RunAsync(() => ShapeTable(table), cancellationToken);

    public async ValueTask<ShapeTableState> RenewShapeTableAsync(string table, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        return await RunAsync(() =>
        {
            var state = ShapeTable(table);
            if (state.Status != ShapeTableStatus.Stale)
            {
                return state;
            }

            var cookie = SchemaCookie();
            if (StoredShapeTable(table) is { } stored && stored.Sql == state.Sql && !SchemaChangedSince(table, stored.Since))
            {
                var touch = Command("UPDATE _tb_shape_chunk SET sealed_at = $cookie WHERE handle = $key AND offset = 0;");
                touch.Parameters.AddWithValue("$cookie", cookie);
                touch.Parameters.AddWithValue("$key", ShapeTablePrefix + table);
                touch.ExecuteNonQuery();
                touch.Parameters.Clear();
                return state with { Status = ShapeTableStatus.Current, Generation = stored.Generation, Since = stored.Since };
            }

            var generation = EntityIds.New().ToString();
            var since = ShapeHead();
            RetireShapeTable(table, null);
            var upsert = Command("INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ($key, 0, $since, $body, $cookie);");
            upsert.Parameters.AddWithValue("$key", ShapeTablePrefix + table);
            upsert.Parameters.AddWithValue("$body", System.Text.Encoding.UTF8.GetBytes(generation + "\n" + state.Sql));
            upsert.Parameters.AddWithValue("$since", since);
            upsert.Parameters.AddWithValue("$cookie", cookie);
            upsert.ExecuteNonQuery();
            upsert.Parameters.Clear();
            return state with { Status = ShapeTableStatus.Current, Generation = generation, Since = since };
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    public ValueTask<ShapeFilterResult> ReadShapeFilterAsync(string table, string generation, ShapeQuery query, CancellationToken cancellationToken) =>
        RunAsync<ShapeFilterResult>(() =>
        {
            if (CurrentShapeColumns(table, generation) is not { } current)
            {
                return new Unavailable("the table changed while resolving its shape; retry later");
            }

            switch (ShapeFilters.Parse(query, current.Columns))
            {
                case Validation error:
                    return error;
                case ShapeFilter { Where: null } filter:
                    return filter;
                case ShapeFilter filter:
                    var matcher = ShapeMatcher(current.Columns, filter.Where);
                    try
                    {
                        _ = ShapeMatches(matcher, new object?[current.Columns.Length]);
                    }
                    catch (SqliteException)
                    {
                        return ShapeFilters.Refused("where is not a valid expression on this table's columns");
                    }

                    return filter;
                default:
                    throw new System.Diagnostics.UnreachableException();
            }
        }, cancellationToken);

    private SqliteCommand ShapeMatcher(string[] columns, string where)
    {
        var projection = string.Join(", ", columns.Select((column, index) => $"$c{index} AS {Quote(column)}"));
        return session.TableCommand($"SELECT 1 FROM (SELECT {projection}) WHERE ({where});", transaction);
    }

    private bool ShapeMatches(SqliteCommand matcher, object?[] values)
    {
        matcher.Parameters.Clear();
        for (var index = 0; index < values.Length; index++)
        {
            matcher.Parameters.AddWithValue($"$c{index}", values[index] ?? DBNull.Value);
        }

        CountRead("shape");
        try
        {
            return matcher.ExecuteScalar() is not null;
        }
        finally
        {
            matcher.Parameters.Clear();
        }
    }

    private ShapeTableState ShapeTable(string table)
    {
        if (store.IsSystem || !ShapeTables.IsValidName(table))
        {
            return new ShapeTableState(ShapeTableStatus.Missing, null, null);
        }

        var schema = Command("SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = $name;");
        schema.Parameters.AddWithValue("$name", table);
        CountRead("shape");
        if (schema.ExecuteScalar() is not string sql)
        {
            return new ShapeTableState(ShapeTableStatus.Missing, null, null);
        }

        if (!sql.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase) || WithoutRowid().IsMatch(sql))
        {
            return new ShapeTableState(ShapeTableStatus.Unsupported, sql, null);
        }

        return StoredShapeTable(table) is { } stored && stored.Sql == sql && stored.Cookie == SchemaCookie()
            ? new ShapeTableState(ShapeTableStatus.Current, sql, stored.Generation, stored.Since)
            : new ShapeTableState(ShapeTableStatus.Stale, sql, null);
    }

    private (string Sql, string Generation, long Since, long Cookie)? StoredShapeTable(string table)
    {
        var stored = Command("SELECT body, end_offset, sealed_at FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
        stored.Parameters.AddWithValue("$key", ShapeTablePrefix + table);
        CountRead("shape");
        using var reader = stored.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var body = System.Text.Encoding.UTF8.GetString(TursoSession.Bytes(reader, 0));
        var split = body.IndexOf('\n', StringComparison.Ordinal);
        return split == 26 && EntityIds.IsValid(body[..split])
            ? (body[(split + 1)..], body[..split], reader.GetInt64(1), reader.GetInt64(2))
            : throw new InvalidDataException("a shape table record is damaged");
    }

    private long SchemaCookie()
    {
        CountRead("shape");
        return Command("PRAGMA schema_version;").ExecuteScalar() is long cookie ? cookie : throw new InvalidDataException("the database has no schema version");
    }

    private bool SchemaChangedSince(string table, long since)
    {
        if (!HasChangeTable())
        {
            return false;
        }

        var changes = Command("SELECT before, after FROM turso_cdc WHERE table_name = 'sqlite_schema' AND change_id > $since ORDER BY change_id;");
        changes.Parameters.AddWithValue("$since", since);
        CountRead("shape");
        using var reader = changes.ExecuteReader();
        while (reader.Read())
        {
            if (SchemaChangeNames(reader, 0, table))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\bWITHOUT\s+ROWID\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WithoutRowid();

    private (string[] Columns, long Since)? CurrentShapeColumns(string table, string generation)
    {
        if (ShapeTable(table) is not { Status: ShapeTableStatus.Current } state || state.Generation != generation)
        {
            return null;
        }

        var command = session.TableCommand($"PRAGMA table_xinfo(\"{table}\");", transaction);
        List<string> columns = [];
        CountRead("shape");
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetInt64(6) is not (1 or 2))
                {
                    columns.Add(reader.GetString(1));
                }
            }
        }

        return columns.Count > 0 ? ([.. columns], state.Since) : throw new InvalidDataException("a shape table has no stored columns");
    }

    private void RetireShapeTable(string table, string? generation)
    {
        if (generation is not null && StoredShapeTable(table)?.Generation != generation)
        {
            return;
        }

        var retire = Command("DELETE FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
        retire.Parameters.AddWithValue("$key", ShapeTablePrefix + table);
        retire.ExecuteNonQuery();
        retire.Parameters.Clear();
    }

    private bool HasChangeTable()
    {
        CountRead("shape");
        return Command("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'turso_cdc';").ExecuteScalar() is not null;
    }

    private long ShapeHead()
    {
        if (!HasChangeTable())
        {
            return 0;
        }

        CountRead("shape");
        return Command("SELECT coalesce((SELECT change_id FROM turso_cdc WHERE change_type = 2 ORDER BY change_id DESC LIMIT 1), 0);")
            .ExecuteScalar() is long head ? head : 0;
    }

    private bool ShapeBoundary(long offset)
    {
        if (!HasChangeTable())
        {
            return offset == 0;
        }

        if (offset == 0)
        {
            CountRead("shape");
            return Command("SELECT coalesce((SELECT change_id FROM turso_cdc ORDER BY change_id LIMIT 1), 1);").ExecuteScalar() is 1L;
        }

        var command = Command("SELECT change_type FROM turso_cdc WHERE change_id = $offset;");
        command.Parameters.AddWithValue("$offset", offset);
        CountRead("shape");
        return command.ExecuteScalar() is 2L;
    }

    public async ValueTask<(long Offset, long Head, ShapeSealedChunk? Chunk)> SealShapeChunkAsync(ShapeSealDefinition shape, long offset, long? through,
        IMemoryBudget memory, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ShapeTables.Validate(shape);
        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        return await RunAsync(() => SealShapeChunk(shape, offset, through, memory, cancellationToken), cancellationToken,
            mutation: true).ConfigureAwait(false);
    }

    private (long Offset, long Head, ShapeSealedChunk? Chunk) SealShapeChunk(ShapeSealDefinition shape, long offset, long? through,
        IMemoryBudget memory, CancellationToken cancellationToken)
    {
        var (columns, since) = CurrentShapeColumns(shape.Table, shape.Generation) ?? throw new ShapeSealOffsetException();
        var output = new ShapeOutput(columns, shape.Filter, shape.Filter?.Where is { } where ? ShapeMatcher(columns, where) : null);
        var head = ShapeHead();
        var stored = Command("SELECT end_offset FROM _tb_shape_chunk WHERE handle = $handle AND offset = $offset;");
        stored.Parameters.AddWithValue("$handle", shape.Handle);
        stored.Parameters.AddWithValue("$offset", offset);
        CountRead("shape");
        if (stored.ExecuteScalar() is long existing)
        {
            return (existing, head, null);
        }

        if (offset == head)
        {
            return (offset, head, null);
        }

        var end = Math.Min(through ?? head, head);
        if (offset > end || !ShapeBoundary(offset))
        {
            throw new ShapeSealOffsetException();
        }

        using var admission = new ShapeMemory(memory, store.Tenant);
        admission.Reserve(256 * 1024);
        var changes = new ArrayBufferWriter<byte>();
        var commit = new ArrayBufferWriter<byte>();
        var covered = offset;
        var rows = 0;
        var commitRows = 0;
        var scanned = 0;
        var after = offset;
        var envelopeBytes = ShapeEnvelope(shape, offset, end, []).Length;
        var stop = false;
        var retired = false;
        while (!stop && after < end)
        {
            var batch = Command("""
                SELECT change_id, change_type, table_name, id, before, after FROM turso_cdc
                WHERE change_id > $after AND change_id <= $end AND (change_type = 2 OR table_name = $table OR table_name = 'sqlite_schema')
                ORDER BY change_id LIMIT $limit;
                """);
            batch.Parameters.AddWithValue("$table", shape.Table);
            batch.Parameters.AddWithValue("$after", after);
            batch.Parameters.AddWithValue("$end", end);
            batch.Parameters.AddWithValue("$limit", ShapeScanBatch);
            CountRead("shape");
            var read = 0;
            using (var reader = batch.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    read++;
                    scanned++;
                    after = reader.GetInt64(0);
                    var type = reader.GetInt64(1);
                    if (type == 2)
                    {
                        if (commitRows > 0)
                        {
                            var size = envelopeBytes + changes.WrittenCount + commit.WrittenCount + 32;
                            if (rows > 0 && (rows + commitRows > ShapeRows || size > ShapeBodyBytes))
                            {
                                stop = true;
                                break;
                            }

                            AppendShapeCommit(changes, commit, after);
                            rows += commitRows;
                        }

                        commit.ResetWrittenCount();
                        commitRows = 0;
                        covered = after;
                        if (rows >= ShapeRows || envelopeBytes + changes.WrittenCount >= ShapeBodyBytes || scanned >= ShapeScanRows)
                        {
                            stop = true;
                            break;
                        }

                        continue;
                    }

                    var table = reader.IsDBNull(2) ? null : reader.GetString(2);
                    if (table == SchemaTable && after > since && SchemaChangeNames(reader, 4, shape.Table))
                    {
                        retired = covered == offset;
                        stop = true;
                        break;
                    }

                    if (table != shape.Table)
                    {
                        continue;
                    }

                    var written = WriteShapeOperation(commit, reader, type, shape.Table, output, admission);
                    if (written is null)
                    {
                        retired = covered == offset;
                        stop = true;
                        break;
                    }

                    commitRows += written.Value ? 1 : 0;
                    if (commit.WrittenCount > ShapeCommitBytes)
                    {
                        if (rows == 0)
                        {
                            throw new ShapeTooLargeException();
                        }

                        stop = true;
                        break;
                    }
                }
            }

            if (read == 0)
            {
                break;
            }
        }

        if (retired)
        {
            RetireShapeTable(shape.Table, shape.Generation);
            return (offset, head, null);
        }

        if (covered == offset)
        {
            return (offset, head, null);
        }

        var body = ShapeEnvelope(shape, offset, covered, changes.WrittenSpan);
        admission.Reserve(body.Length);
        PublishShapeLineage(shape.Handle, shape.Table, shape.Generation, shape.Incarnation);
        var insert = Command("INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ($handle, $offset, $end, $body, $at);");
        insert.Parameters.AddWithValue("$handle", shape.Handle);
        insert.Parameters.AddWithValue("$offset", offset);
        insert.Parameters.AddWithValue("$end", covered);
        insert.Parameters.AddWithValue("$body", body);
        var sealedAt = Member.Clock.UtcNow.ToUnixTimeMilliseconds();
        insert.Parameters.AddWithValue("$at", sealedAt);
        insert.ExecuteNonQuery();
        insert.Parameters.Clear();

        var retained = new ShapeMemory(memory, store.Tenant);
        admission.TransferTo(retained);
        return (covered, head, new ShapeSealedChunk(offset, covered, covered, body, retained, sealedAt));
    }

    private static void AppendShapeCommit(ArrayBufferWriter<byte> changes, ArrayBufferWriter<byte> commit, long offset)
    {
        if (changes.WrittenCount > 0)
        {
            changes.Write(","u8);
        }

        changes.Write("{\"offset\":\""u8);
        changes.Write(System.Text.Encoding.ASCII.GetBytes(ShapeNumber(offset)));
        changes.Write("\",\"ops\":["u8);
        changes.Write(commit.WrittenSpan);
        changes.Write("]}"u8);
    }

    private static bool SchemaChangeNames(SqliteDataReader reader, int first, string table)
    {
        foreach (var column in (ReadOnlySpan<int>)[first, first + 1])
        {
            if (!reader.IsDBNull(column) && SqliteRecord.Decode(TursoSession.Bytes(reader, column), columns: 3) is [_, _, string name]
                && string.Equals(name, table, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record ShapeOutput(string[] Columns, ShapeFilter? Filter, SqliteCommand? Matcher)
    {
        public int[] Selected { get; } = Filter is null
            ? [.. Enumerable.Range(0, Columns.Length)]
            : [.. Filter.Columns.Select(column => Array.IndexOf(Columns, column))];
    }

    private bool? WriteShapeOperation(ArrayBufferWriter<byte> commit, SqliteDataReader reader, long type, string table, ShapeOutput output, ShapeMemory admission)
    {
        if (reader.IsDBNull(3) || reader.GetValue(3) is not long key)
        {
            throw new InvalidDataException("a captured row change has no integer row id");
        }

        if (type is not (0 or 1 or -1))
        {
            throw new InvalidDataException($"a captured change has change type {type}, which no capture writes");
        }

        object?[]? after = null;
        if (type is 0 or 1)
        {
            if (reader.IsDBNull(5))
            {
                after = CurrentShapeRow(table, output.Columns, key, admission);
                type = after is null ? -1 : type;
            }
            else if (ShapeImage(reader, 5, output.Columns.Length, admission) is { } image)
            {
                after = image;
            }
            else
            {
                return null;
            }
        }

        string? move = null;
        if (output.Filter is not null)
        {
            object?[]? before = null;
            if (type is 0 or -1 && !reader.IsDBNull(4))
            {
                before = ShapeImage(reader, 4, output.Columns.Length, admission);
                if (before is null)
                {
                    return null;
                }
            }

            bool? was = before is null ? null : output.Matcher is null || ShapeMatches(output.Matcher, before);
            var now = after is not null && (output.Matcher is null || ShapeMatches(output.Matcher, after));
            switch (type, was, now)
            {
                case (1, _, false) or (0, false, false) or (-1, false, _):
                    return false;
                case (0, true, true) when output.Selected.All(index =>
                    System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(before![index], after![index])):
                    return false;
                case (0, false, true):
                    (type, move) = (1, "in");
                    break;
                case (0, true, false):
                    (type, after, move) = (-1, null, "out");
                    break;
                case (0, null, false):
                    (type, after) = (-1, null);
                    break;
            }
        }

        if (commit.WrittenCount > 0)
        {
            commit.Write(","u8);
        }

        using var writer = new Utf8JsonWriter(commit);
        writer.WriteStartObject();
        writer.WriteString("op", type switch { 1 => "insert", 0 => "update", _ => "delete" });
        writer.WriteString("key", ShapeNumber(key));
        if (after is not null)
        {
            writer.WriteStartObject("row");
            foreach (var index in output.Selected)
            {
                writer.WritePropertyName(output.Columns[index]);
                WriteShapeValue(writer, after[index]);
            }

            writer.WriteEndObject();
        }

        if (move is not null)
        {
            writer.WriteString("move", move);
        }

        writer.WriteEndObject();
        writer.Flush();
        return true;
    }

    private static object?[]? ShapeImage(SqliteDataReader reader, int column, int columns, ShapeMemory admission)
    {
        var image = TursoSession.Bytes(reader, column);
        admission.Reserve(checked(image.Length * 6L + 256));
        var values = SqliteRecord.Decode(image, columns: columns + 1);
        return values.Length == columns ? values : null;
    }

    private object?[]? CurrentShapeRow(string table, string[] columns, long key, ShapeMemory admission)
    {
        var size = session.TableCommand($"SELECT {string.Join(" + ", columns.Select(column => $"coalesce(length(CAST({Quote(column)} AS BLOB)), 0)"))} FROM {Quote(table)} WHERE rowid = $key;", transaction);
        size.Parameters.Clear();
        size.Parameters.AddWithValue("$key", key);
        CountRead("shape");
        if (size.ExecuteScalar() is not long bytes)
        {
            return null;
        }

        admission.Reserve(checked(bytes * 6L + 256));
        var row = session.TableCommand($"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(table)} WHERE rowid = $key;", transaction);
        row.Parameters.Clear();
        row.Parameters.AddWithValue("$key", key);
        CountRead("shape");
        using var reader = row.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var values = new object?[columns.Length];
        for (var index = 0; index < columns.Length; index++)
        {
            values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        }

        return values;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal static void WriteShapeValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                writer.WriteNullValue();
                break;
            case long integer:
                writer.WriteNumberValue(integer);
                break;
            case double real when double.IsFinite(real):
                writer.WriteNumberValue(real);
                break;
            case double:
                throw new InvalidDataException("a shape value is not a finite number");
            case string text:
                writer.WriteStringValue(text);
                break;
            case byte[] blob:
                writer.WriteStartObject();
                writer.WriteBase64String("base64", blob);
                writer.WriteEndObject();
                break;
            default:
                throw new InvalidDataException("a shape value has an unknown storage class");
        }
    }

    private static byte[] ShapeEnvelope(ShapeSealDefinition shape, long offset, long covered, ReadOnlySpan<byte> changes, bool upToDate = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("shape_id", shape.ShapeId);
        writer.WriteString("handle", shape.Handle);
        writer.WriteString("offset", ShapeNumber(offset));
        writer.WriteString("next_offset", ShapeNumber(covered));
        writer.WriteString("values_at", ShapeNumber(covered));
        writer.WriteBoolean("up_to_date", upToDate);
        writer.WriteStartArray("changes");
        writer.Flush();
        buffer.Write(changes);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public ValueTask<ShapeSealedChunk?> ReadShapeChunkAsync(string handle, long offset, IMemoryBudget memory,
        CancellationToken cancellationToken) => RunAsync(() =>
    {
        var size = Command("SELECT octet_length(body) FROM _tb_shape_chunk WHERE handle = $handle AND offset = $offset;");
        size.Parameters.AddWithValue("$handle", handle);
        size.Parameters.AddWithValue("$offset", offset);
        CountRead("shape");
        if (size.ExecuteScalar() is not long bytes)
        {
            return null;
        }

        if (bytes is <= 0 or > ShapeCommitBytes + ShapeBodyBytes)
        {
            throw new InvalidDataException("a sealed shape chunk exceeds its body bound");
        }

        var admission = memory.TryReserve(MemoryLane.Foreground, checked(bytes + 4096), store.Tenant) ?? throw new QosMemoryException();
        try
        {
            var command = Command("SELECT end_offset, body, sealed_at FROM _tb_shape_chunk WHERE handle = $handle AND offset = $offset;");
            command.Parameters.AddWithValue("$handle", handle);
            command.Parameters.AddWithValue("$offset", offset);
            CountRead("shape_body");
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidDataException("a sealed shape chunk disappeared within a read transaction");
            }

            var end = reader.GetInt64(0);
            return new ShapeSealedChunk(offset, end, end, TursoSession.Bytes(reader, 1), admission, reader.GetInt64(2));
        }
        catch
        {
            admission.Dispose();
            throw;
        }
    }, cancellationToken);

    private static string ShapeNumber(long offset) => offset.ToString(CultureInfo.InvariantCulture);

    private sealed class ShapeMemory(IMemoryBudget memory, TenantContext? tenant) : IMemoryReservation
    {
        private readonly List<IMemoryReservation> _reservations = [];

        public long Bytes => _reservations.Sum(reservation => reservation.Bytes);

        public void Reserve(long bytes) => _reservations.Add(memory.TryReserve(MemoryLane.Bulk, bytes, tenant) ?? throw new QosMemoryException());

        public void TransferTo(ShapeMemory owner)
        {
            owner._reservations.AddRange(_reservations);
            _reservations.Clear();
        }

        public void Dispose()
        {
            foreach (var reservation in _reservations)
            {
                reservation.Dispose();
            }
        }
    }
}

internal sealed class TursoShapeStorage(TursoStorageEngine engine) : IShapeStorage
{
    public async ValueTask<IStorageTransaction> BeginTransactionAsync(IDatabaseStore store, bool write, CancellationToken cancellationToken)
    {
        var underlying = store.Tenant is { } tenant
            ? await engine.OpenDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false)
            : throw new InvalidOperationException("shapes belong to client databases");
        if (underlying is null || underlying.Incarnation != store.Incarnation || store.IsClosed)
        {
            throw new ShapeSealOffsetException();
        }

        return await underlying.BeginTransactionAsync(write, cancellationToken).ConfigureAwait(false);
    }
}
