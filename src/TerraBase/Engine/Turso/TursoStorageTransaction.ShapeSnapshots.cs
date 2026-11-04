using System.Buffers;
using System.Text.Json;
using TerraBase.Features.Memory;
using TerraBase.Features.Shapes;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction : IShapeSnapshotStorage
{
    private const string CompleteSnapshot = """
        SELECT offset, sealed_at FROM _tb_shape_snapshot
        WHERE handle = $handle AND next_id IS NULL ORDER BY offset DESC LIMIT 1;
        """;

    public ValueTask<ShapeSnapshot?> NewestShapeSnapshotAsync(string handle, CancellationToken cancellationToken) =>
        RunAsync(() => NewestShapeSnapshot(handle), cancellationToken);

    private ShapeSnapshot? NewestShapeSnapshot(string handle)
    {
        var command = Command(CompleteSnapshot);
        command.Parameters.AddWithValue("$handle", handle);
        CountRead("shape");
        using var reader = command.ExecuteReader();
        return reader.Read() ? new ShapeSnapshot(reader.GetInt64(0), reader.GetInt64(1)) : null;
    }

    public ValueTask<bool> ShapeSnapshotDueAsync(string handle, long now, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var latest = NewestShapeSnapshot(handle);
            if (latest is null || latest.Offset < StoredShapeFloor(handle))
            {
                return true;
            }

            if (ShapeHead() == latest.Offset)
            {
                return false;
            }

            if (now - latest.SealedAt >= ShapeSnapshots.ResealAge.TotalMilliseconds)
            {
                return true;
            }

            var pages = Command("SELECT count(*) FROM _tb_shape_snapshot WHERE handle = $handle AND offset = $offset;");
            pages.Parameters.AddWithValue("$handle", handle);
            pages.Parameters.AddWithValue("$offset", latest.Offset);
            CountRead("shape");
            var threshold = Math.Min(ShapeSnapshots.ResealChunks, ShapeSnapshots.ResealChunksPerPage * (long)pages.ExecuteScalar()!);
            var command = Command("""
                SELECT count(*) FROM (SELECT 1 FROM _tb_shape_chunk
                    WHERE handle = $handle AND end_offset > $offset LIMIT $limit);
                """);
            command.Parameters.AddWithValue("$handle", handle);
            command.Parameters.AddWithValue("$offset", latest.Offset);
            command.Parameters.AddWithValue("$limit", threshold);
            CountRead("shape");
            return (long)command.ExecuteScalar()! >= threshold;
        }, cancellationToken);

    public ValueTask<long> ShapeSnapshotHeadAsync(CancellationToken cancellationToken) => RunAsync(ShapeHead, cancellationToken);

    public ValueTask<ShapeSnapshotPage> BuildShapeSnapshotPageAsync(
        ShapeSealDefinition shape, long offset, int page, long? afterRowid, IMemoryBudget memory, TimeProvider time,
        CancellationToken cancellationToken) =>
        RunAsync(() => BuildShapeSnapshotPage(shape, offset, page, afterRowid, memory, time, cancellationToken), cancellationToken);

    private ShapeSnapshotPage BuildShapeSnapshotPage(
        ShapeSealDefinition shape, long offset, int page, long? afterRowid, IMemoryBudget memory, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (CurrentShapeColumns(shape.Table, shape.Generation) is not { } current || ShapeHead() != offset)
        {
            throw new ShapeSealOffsetException();
        }

        var columns = current.Columns;

        var workspace = memory.TryReserve(MemoryLane.Bulk, ShapeSnapshots.WorkspaceBytes, store.Tenant) ?? throw new StorageBusyException();
        IMemoryReservation? rowMemory = null;
        try
        {
            var operations = new ArrayBufferWriter<byte>(ShapeSnapshots.PageBytes);
            var count = 0;
            var last = afterRowid;
            var started = time.GetTimestamp();
            var envelopeBytes = ShapeSnapshots.EncodePage(shape, offset, page, more: true, []).Length;
            var more = false;
            var select = $"SELECT rowid, {string.Join(", ", columns.Select(Quote))} FROM {Quote(shape.Table)}";
            while (!more)
            {
                var rows = session.TableCommand(last is null
                    ? select + " ORDER BY rowid LIMIT $limit;"
                    : select + " WHERE rowid > $after ORDER BY rowid LIMIT $limit;", transaction);
                rows.Parameters.Clear();
                rows.Parameters.AddWithValue("$limit", 128);
                if (last is { } after)
                {
                    rows.Parameters.AddWithValue("$after", after);
                }

                var read = 0;
                CountRead("shape");
                using (var reader = rows.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (count == ShapeSnapshots.PageRows || (count > 0 && time.GetElapsedTime(started) >= ShapeSnapshots.PageDeadline))
                        {
                            more = true;
                            break;
                        }

                        read++;
                        var operation = new ArrayBufferWriter<byte>();
                        using (var writer = new Utf8JsonWriter(operation))
                        {
                            writer.WriteStartObject();
                            writer.WriteString("op", "insert");
                            writer.WriteString("key", ShapeNumber(reader.GetInt64(0)));
                            writer.WriteStartObject("row");
                            for (var index = 0; index < columns.Length; index++)
                            {
                                writer.WritePropertyName(columns[index]);
                                WriteShapeValue(writer, reader.IsDBNull(index + 1) ? null : reader.GetValue(index + 1));
                            }

                            writer.WriteEndObject();
                            writer.WriteEndObject();
                        }

                        var candidate = envelopeBytes + operations.WrittenCount + operation.WrittenCount + 1;
                        if (count > 0 && candidate > ShapeSnapshots.PageBytes)
                        {
                            more = true;
                            break;
                        }

                        if (candidate > ShapeSnapshots.SingletonBytes)
                        {
                            throw new ShapeTooLargeException();
                        }

                        if (candidate > ShapeSnapshots.PageBytes)
                        {
                            rowMemory = memory.TryReserve(MemoryLane.Bulk, checked(candidate * 2L), store.Tenant) ?? throw new StorageBusyException();
                        }

                        if (count > 0)
                        {
                            operations.Write(","u8);
                        }

                        operations.Write(operation.WrittenSpan);
                        count++;
                        last = reader.GetInt64(0);
                    }
                }

                if (read == 0)
                {
                    break;
                }
            }

            var body = ShapeSnapshots.EncodePage(shape, offset, page, more, operations.WrittenSpan);
            var resultMemory = memory.TryReserve(MemoryLane.Bulk, checked(body.Length * 3L + 4096), store.Tenant) ?? throw new StorageBusyException();
            return new ShapeSnapshotPage(body, more ? last : null, resultMemory);
        }
        finally
        {
            rowMemory?.Dispose();
            workspace.Dispose();
        }
    }

    public async ValueTask InsertShapeSnapshotPageAsync(
        ShapeSealDefinition shape, long offset, int page, ShapeSnapshotPage body, long sealedAt, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        _ = await RunAsync(() =>
        {
            PublishShapeLineage(shape.Handle, shape.Table, shape.Generation, shape.Incarnation);
            var command = Command("""
                INSERT INTO _tb_shape_snapshot (handle, offset, page, next_id, body, sealed_at)
                VALUES ($handle, $offset, $page, $next, $body, $sealed);
                """);
            command.Parameters.AddWithValue("$handle", shape.Handle);
            command.Parameters.AddWithValue("$offset", offset);
            command.Parameters.AddWithValue("$page", page);
            command.Parameters.AddWithValue("$next", body.NextRowid is { } next ? ShapeNumber(next) : DBNull.Value);
            command.Parameters.AddWithValue("$body", body.Body.ToArray());
            command.Parameters.AddWithValue("$sealed", sealedAt);
            try
            {
                return command.ExecuteNonQuery();
            }
            finally
            {
                command.Parameters.Clear();
            }
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    public async ValueTask DeleteIncompleteShapeSnapshotsAsync(string handle, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        _ = await RunAsync(() =>
        {
            var command = Command("""
                DELETE FROM _tb_shape_snapshot WHERE handle = $handle AND offset NOT IN
                    (SELECT offset FROM _tb_shape_snapshot WHERE handle = $handle AND next_id IS NULL);
                """);
            command.Parameters.AddWithValue("$handle", handle);
            return command.ExecuteNonQuery();
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    public ValueTask<ShapeSnapshotPage?> ReadShapeSnapshotPageAsync(
        string handle, long offset, int page, IMemoryBudget memory, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var command = Command("""
                SELECT octet_length(body), next_id IS NOT NULL FROM _tb_shape_snapshot
                WHERE handle = $handle AND offset = $offset AND page = $page
                    AND EXISTS (SELECT 1 FROM _tb_shape_snapshot WHERE handle = $handle AND offset = $offset AND next_id IS NULL);
                """);
            command.Parameters.AddWithValue("$handle", handle);
            command.Parameters.AddWithValue("$offset", offset);
            command.Parameters.AddWithValue("$page", page);
            long bytes;
            bool more;
            CountRead("shape");
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                bytes = reader.GetInt64(0);
                more = reader.GetInt64(1) != 0;
            }

            if (bytes is < 0 or > ShapeSnapshots.SingletonBytes)
            {
                throw new InvalidDataException("a stored snapshot page exceeds its byte limit");
            }

            var reservation = memory.TryReserve(MemoryLane.Foreground, checked(bytes * 3 + 4096), store.Tenant) ?? throw new StorageBusyException();
            try
            {
                var body = Command("SELECT body FROM _tb_shape_snapshot WHERE handle = $handle AND offset = $offset AND page = $page;");
                body.Parameters.AddWithValue("$handle", handle);
                body.Parameters.AddWithValue("$offset", offset);
                body.Parameters.AddWithValue("$page", page);
                CountRead("shape_body");
                using var reader = body.ExecuteReader();
                reader.Read();
                return new ShapeSnapshotPage(TursoSession.Bytes(reader, 0), more ? 0 : null, reservation);
            }
            catch
            {
                reservation.Dispose();
                throw;
            }
        }, cancellationToken);
}

internal sealed class TursoShapeSnapshotTransactions(TursoStorageEngine engine) : IShapeSnapshotTransactions
{
    public async ValueTask<IStorageTransaction> BeginAsync(IDatabaseStore store, bool write, CancellationToken cancellationToken)
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
