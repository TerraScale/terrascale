using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Documents;
using TerraBase.Features.Memory;
using TerraBase.Features.Shapes;
using TerraBase.Primitives;

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

    public ValueTask<long> ShapeSnapshotFloorAsync(string handle, CancellationToken cancellationToken) =>
        RunAsync(() => ShapeFloor(handle), cancellationToken);

    public ValueTask<bool> ShapeSnapshotDueAsync(string handle, long now, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var latest = NewestShapeSnapshot(handle);

            if (latest is null || latest.Offset < ShapeFloor(handle))
            {
                return true;
            }

            CountRead("shape");
            if ((long)Command("SELECT seq FROM _tb_feed_sequence WHERE id = 1;").ExecuteScalar()! == latest.Offset)
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

    public ValueTask<(long Offset, string Cursor)> ShapeSnapshotHeadAsync(
        ReadOnlyMemory<byte> resumeScope, DateTimeOffset now, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            CountRead("shape");
            var offset = (long)Command("SELECT seq FROM _tb_feed_sequence WHERE id = 1;").ExecuteScalar()!;

            Dictionary<ReplicaId, Hlc> stamps = [];
            Dictionary<ReplicaId, FeedPrefix> prefixes = [];
            List<(ReplicaId Origin, Hlc Floor)> floors = [];
            CountRead("shape");
            using (var reader = Command("SELECT origin, stamp FROM _tb_feed_floor LIMIT 257;").ExecuteReader())
            {
                while (reader.Read())
                {
                    if (floors.Count == ChangeCursor.MaxOrigins)
                    {
                        throw new InvalidDataException("a shape snapshot cursor has too many origins");
                    }

                    floors.Add((new ReplicaId(unchecked((ulong)reader.GetInt64(0))), new Hlc((ulong)reader.GetInt64(1))));
                }
            }

            foreach (var (origin, floor) in floors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var command = Command(FeedHead);
                command.Parameters.AddWithValue("$origin", unchecked((long)origin.Value));
                CountRead("shape");
                if (command.ExecuteScalar() is not long stamp)
                {
                    continue;
                }

                var at = new Hlc((ulong)stamp);
                stamps[origin] = at;
                prefixes[origin] = PrefixThrough(origin, floor, at, cancellationToken, "shape");
            }

            var cursor = ChangeCursor.Encode(new FeedCursor(stamps, FeedCursor.At(now)) { Issuer = Member.Id, Prefixes = prefixes });
            var proof = Base64Url.DecodeFromChars(cursor);
            var envelope = new byte[17 + proof.Length];
            envelope[0] = 1;
            resumeScope.Span.CopyTo(envelope.AsSpan(1, 16));
            proof.CopyTo(envelope, 17);
            return (offset, Base64Url.EncodeToString(envelope));
        }, cancellationToken);

    public ValueTask<ShapeSnapshotPage> BuildShapeSnapshotPageAsync(
        ShapeSnapshotDefinition shape, long offset, string? afterId, string cursor, IMemoryBudget memory, TimeProvider time,
        CancellationToken cancellationToken) =>
        RunAsync(() => BuildShapeSnapshotPage(shape, offset, afterId, cursor, memory, time, cancellationToken), cancellationToken);

    private ShapeSnapshotPage BuildShapeSnapshotPage(
        ShapeSnapshotDefinition shape, long offset, string? afterId, string cursor, IMemoryBudget memory, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (store.IsSystem)
        {
            throw new InvalidDataException("a shape snapshot requires an existing client collection");
        }
        CountRead("shape");
        if (Command("SELECT entity_id FROM collections WHERE name = $collection;", shape.Collection).ExecuteScalar()
            is not string generation || generation != shape.CollectionGeneration)
        {
            throw new ShapeSealOffsetException();
        }

        var workspace = memory.TryReserve(MemoryLane.Bulk, ShapeSnapshots.WorkspaceBytes, store.Tenant) ?? throw new StorageBusyException();
        IMemoryReservation? singletonMemory = null;
        try
        {
            var operations = new ArrayBufferWriter<byte>(ShapeSnapshots.PageBytes);
            operations.Write("["u8);
            var count = 0;
            var examined = 0;
            var last = afterId;
            var started = time.GetTimestamp();
            var envelopeBytes = ShapeSnapshots.EncodePage(shape, offset, afterId, "", cursor, "[]"u8).Length;
            var more = false;
            while (!more)
            {

                var batch = ShapeSnapshotRows(shape.Collection, shape.OwnerFilter, last,
                    count == ShapeSnapshots.PageDocuments ? 1 : Math.Min(128, ShapeSnapshots.PageExamined - examined + 1), cancellationToken);
                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var (id, size, version) in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (examined == ShapeSnapshots.PageExamined || count == ShapeSnapshots.PageDocuments
                        || (examined > 0 && time.GetElapsedTime(started) >= ShapeSnapshots.PageDeadline))
                    {
                        more = true;
                        break;
                    }

                    using var rowMemory = memory.TryReserve(MemoryLane.Bulk, checked(size * 8 + 32 * 1024), store.Tenant) ?? throw new StorageBusyException();
                    var bodyQuery = TableCommand("SELECT CAST(doc AS BLOB) FROM {0} WHERE id = $id;", shape.Collection, id);
                    StoredDocument document;
                    CountRead("shape");
                    using (var bodyReader = bodyQuery.ExecuteReader())
                    {
                        if (!bodyReader.Read())
                        {
                            throw new InvalidDataException("a snapshot document has no materialized value");
                        }

                        document = new StoredDocument(id, version, TursoSession.Bytes(bodyReader, 0), Scope);
                    }

                    DocumentJson.ValidateStored(document.Json.Span);
                    if (document.Version <= 0)
                    {
                        throw new InvalidDataException("a snapshot document has no live state version");
                    }

                    var matches = shape.Predicate?.Invoke(document) ?? true;
                    var operation = matches ? ShapeSnapshots.Upsert(shape.Collection, document) : [];
                    var candidateBytes = envelopeBytes + Base64Url.GetEncodedLength(Encoding.UTF8.GetByteCount(id))
                        + operations.WrittenCount - 1 + operation.Length + (matches && count > 0 ? 1 : 0);
                    if (count > 0 && candidateBytes > ShapeSnapshots.PageBytes)
                    {
                        more = true;
                        break;
                    }

                    if (matches)
                    {
                        if (candidateBytes > ShapeSnapshots.SingletonBytes)
                        {
                            throw new InvalidDataException("a snapshot singleton page exceeds 8 MiB");
                        }

                        if (count > 0)
                        {
                            operations.Write(","u8);
                        }

                        if (candidateBytes > ShapeSnapshots.PageBytes)
                        {
                            singletonMemory = memory.TryReserve(MemoryLane.Bulk, checked(size * 8 + 32 * 1024), store.Tenant)
                                ?? throw new StorageBusyException();
                        }

                        operations.Write(operation);
                        count++;
                    }

                    last = id;
                    examined++;
                }
            }

            operations.Write("]"u8);
            var next = more ? last : null;
            var bytes = ShapeSnapshots.EncodePage(shape, offset, afterId, next, cursor, "[]"u8).Length + operations.WrittenCount - 2;
            if (bytes > ShapeSnapshots.SingletonBytes || (count > 1 && bytes > ShapeSnapshots.PageBytes))
            {
                throw new InvalidDataException("a shape snapshot page exceeds its byte limit");
            }

            var resultMemory = memory.TryReserve(MemoryLane.Bulk, checked(bytes * 3L + 4096), store.Tenant) ?? throw new StorageBusyException();
            try
            {
                return new ShapeSnapshotPage(ShapeSnapshots.EncodePage(shape, offset, afterId, next, cursor, operations.WrittenSpan), next, resultMemory);
            }
            catch
            {
                resultMemory.Dispose();
                throw;
            }
        }
        finally
        {
            singletonMemory?.Dispose();
            workspace.Dispose();
        }
    }

    private List<(string Id, long Bytes, long Version)> ShapeSnapshotRows(
        string collection, DocumentFilter? ownerFilter, string? afterId, int limit, CancellationToken cancellationToken)
    {
        var sql = afterId is null ? """
            SELECT CAST(d.id AS BLOB), octet_length(d.doc),
                (SELECT s.stamp FROM _tb_state s WHERE s.collection = $collection AND s.id = d.id), octet_length(d.id)
            FROM {0} d ORDER BY d.id LIMIT $limit;
            """ : """
            SELECT CAST(d.id AS BLOB), octet_length(d.doc),
                (SELECT s.stamp FROM _tb_state s WHERE s.collection = $collection AND s.id = d.id), octet_length(d.id)
            FROM {0} d WHERE d.id > $after ORDER BY d.id LIMIT $limit;
            """;
        string? ownerPath = null;
        string? ownerSubject = null;
        if (ownerFilter is not null)
        {
            if (ownerFilter is not CompareFilter { Op: Comparison.Equal, Path.Segments: { } segments, Value: { Kind: ScalarKind.Text, Value: string actor } })
            {
                throw new InvalidDataException("snapshot ownership requires a trusted subject equality");
            }

            ownerPath = "$" + string.Concat(segments.Select(segment => ".\""
                + JsonEncodedText.Encode(segment, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value + "\""));
            ownerSubject = actor;
            var owner = "json_type(d.doc, $ownerPath) IS 'text' AND json_extract(d.doc, $ownerPath) = $ownerSubject";
            sql = sql.Replace(" ORDER BY", afterId is null ? $" WHERE {owner} ORDER BY" : $" AND {owner} ORDER BY", StringComparison.Ordinal);
        }

        var rows = TableCommand(sql, collection);
        rows.Parameters.AddWithValue("$limit", limit);
        if (afterId is not null)
        {
            rows.Parameters.AddWithValue("$after", afterId);
        }
        if (ownerPath is not null)
        {
            rows.Parameters.AddWithValue("$ownerPath", ownerPath);
            rows.Parameters.AddWithValue("$ownerSubject", ownerSubject!);
        }

        List<(string, long, long)> batch = [];
        CountRead("shape");
        using var reader = rows.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = reader.GetInt64(1);
            if (size is < 0 or > ShapeSnapshots.SingletonBytes || reader.IsDBNull(2)
                || reader.GetInt64(3) is < 1 or > 1024)
            {
                throw new InvalidDataException("a snapshot document is oversized or has no state version");
            }

            var id = StrictUtf8.GetString(TursoSession.Bytes(reader, 0));
            if (id.Length is < 1 or > 256 || id.Any(char.IsControl))
            {
                throw new InvalidDataException("a snapshot document has an invalid id");
            }

            batch.Add((id, size, reader.GetInt64(2)));
        }

        return batch;
    }

    public async ValueTask InsertShapeSnapshotPageAsync(
        ShapeSnapshotDefinition shape, long offset, int page, ShapeSnapshotPage body, long sealedAt, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        _ = await RunAsync(() =>
        {

            var handle = shape.Handle;
            PublishShapeLineage(handle, shape.Collection, shape.CollectionGeneration, shape.Incarnation);
            var command = Command("""
                INSERT INTO _tb_shape_snapshot (handle, offset, page, next_id, body, sealed_at)
                VALUES ($handle, $offset, $page, $next, $body, $sealed);
                """);
            command.Parameters.AddWithValue("$handle", handle);
            command.Parameters.AddWithValue("$offset", offset);
            command.Parameters.AddWithValue("$page", page);
            command.Parameters.AddWithValue("$next", (object?)body.NextId ?? DBNull.Value);
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
        string handle, long offset, string? afterId, IMemoryBudget memory, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var command = Command("""
                SELECT page, octet_length(body), next_id, coalesce(octet_length(next_id), 0) FROM _tb_shape_snapshot
                WHERE handle = $handle AND offset = $offset
                    AND page = CASE WHEN $after IS NULL THEN 0 ELSE
                        (SELECT page + 1 FROM _tb_shape_snapshot WHERE handle = $handle AND offset = $offset AND next_id = $after LIMIT 1) END
                    AND EXISTS (SELECT 1 FROM _tb_shape_snapshot WHERE handle = $handle AND offset = $offset AND next_id IS NULL);
                """);
            command.Parameters.AddWithValue("$handle", handle);
            command.Parameters.AddWithValue("$offset", offset);
            command.Parameters.AddWithValue("$after", (object?)afterId ?? DBNull.Value);
            int page;
            long bytes;
            string? next;
            CountRead("shape");
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                page = reader.GetInt32(0);
                bytes = reader.GetInt64(1);
                if (reader.GetInt64(3) is < 0 or > 1024)
                {
                    throw new InvalidDataException("a stored snapshot keyset exceeds its byte limit");
                }

                next = reader.IsDBNull(2) ? null : reader.GetString(2);
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
                return new ShapeSnapshotPage(TursoSession.Bytes(reader, 0), next, reservation);
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
