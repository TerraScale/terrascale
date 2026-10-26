using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Documents;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Shapes;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction : IShapeStorageTransaction
{
    private const int ShapeRows = 4096;
    private const int ShapeBodyBytes = 1024 * 1024;
    private const int ShapeCommitBytes = 8 * 1024 * 1024;
    // A JSON-escaped id can expand sixfold; output buffers and persisted copies coexist during sealing.
    private const int ShapeIdMemoryPerByte = 6 * 8;
    // Capture retains raw images, UTF-16 document strings and output copies.
    private const int ShapeCaptureMemoryPerByte = 16;
    // Compact state fields expand into field objects and maps, including normalization's second map.
    private const int ShapeCaptureStateMemoryPerByte = 64;
    // JsonDocument uses 12 bytes per token; three capacities can coexist while its metadata buffer grows.
    private const int ShapeCaptureMemoryPerToken = 12 * 3;

    public async ValueTask<(long Offset, long Head, ShapeSealedChunk? Chunk)> SealShapeChunkAsync(ShapeSealDefinition shape, long offset, long? through,
        bool remote, IMemoryBudget memory, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (shape.ResumeScope.Length != 16 || shape.ShapeId.Length != 32 || shape.Handle.Length != 19
            || !EntityIds.IsValid(shape.CollectionGeneration) || !DatabaseName.TryParse(shape.Collection, out _))
        {
            throw new ArgumentException("a shape needs its lineage identity, collection and 16-byte resume scope", nameof(shape));
        }

        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        return await RunAsync(() => SealShapeChunk(shape, offset, through, remote, memory, cancellationToken), cancellationToken,
            mutation: true).ConfigureAwait(false);
    }

    // With `remote`, seals only while the remote durable frontier is the head this transaction materializes at, so the
    // chunk's covered offset and values_at are both durable when it is stored (shape-reads.md section 10).
    private (long Offset, long Head, ShapeSealedChunk? Chunk) SealShapeChunk(ShapeSealDefinition shape, long offset, long? through,
        bool remote, IMemoryBudget memory, CancellationToken cancellationToken)
    {
        CountRead("shape");
        if (Command("SELECT entity_id FROM collections WHERE name = $collection;", shape.Collection).ExecuteScalar()
            is not string generation || generation != shape.CollectionGeneration)
        {
            throw new ShapeSealOffsetException();
        }
        CountRead("shape");
        var head = (long)Command("SELECT seq FROM _tb_feed_sequence WHERE id = 1;").ExecuteScalar()!;
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

        // In a cell, values read at the head are durable for a document with no feed row above F, so a chunk covers commits
        // through F whose documents have none, at values_at = F. Under steady writes the head never equals F.
        var frontier = remote ? ShapeFrontierOrHead(remote: true) : head;
        if (frontier < head && (frontier <= offset || head - frontier > ShapeRows))
        {
            // Nothing sealed: no commit past offset is held by another region yet, or too many are unheld to check.
            return (offset, head, null);
        }

        var end = Math.Min(through ?? head, frontier);
        if (offset > end || !ShapeBoundary(offset))
        {
            throw new ShapeSealOffsetException();
        }

        ValidateShapeCapture(memory, cancellationToken);
        using var admission = new ShapeMemory(memory, store.Tenant);
        admission.Reserve(256 * 1024);
        var unsettled = frontier < head ? ShapeUnsettled(shape.Collection, frontier, admission) : null;
        if (unsettled is { Count: > 0 } && ReadUniqueConstraints(shape.Collection).Count > 0)
        {
            // A uniqueness write can hide or show another document of its collection without that document's own feed row.
            return (offset, head, null);
        }
        var changes = new ArrayBufferWriter<byte>();
        var covered = offset;
        var count = 0;
        var emitted = 0;
        // Account for the largest v1 resume envelope without rehashing retained history for every candidate commit.
        var resumeSize = new string('0', Base64Url.GetEncodedLength(17 + ChangeCursor.MaxBytes));
        var envelopeBytes = ShapeEnvelope(shape, offset, head, frontier, resumeSize, []).Length;
        while (covered < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = ShapeCommit(covered, end, cancellationToken);
            if (count > 0 && count + next.Rows > ShapeRows)
            {
                break;
            }
            using var candidate = new ShapeMemory(memory, store.Tenant);
            byte[]? change;
            try
            {
                candidate.Reserve(ShapeChangeBytes(shape, covered, next.End));
                change = ShapeChange(shape, covered, next.End, unsettled, cancellationToken);
            }
            catch (Exception error) when (count > 0 && error is ShapeTooLargeException or QosMemoryException)
            {
                // Publish the fitting prefix first; the next seal refuses at the oversized commit itself.
                break;
            }
            if (change is null)
            {
                // A document of this commit has a write above F: publish the prefix, and the rest waits for F.
                break;
            }
            // Include envelope and cursor overhead in the limit, not only document bytes.
            var size = envelopeBytes + changes.WrittenCount + change.Length + (emitted > 0 && change.Length > 0 ? 1 : 0);
            if (count > 0 && size > ShapeBodyBytes)
            {
                break;
            }
            if (count == 0 && size > ShapeCommitBytes)
            {
                // The singleton bound is exact: a smaller real cursor must not turn a fitting commit into a refusal.
                var exact = ShapeEnvelope(shape, offset, next.End, frontier, ShapeResume(shape.ResumeScope.Span, next.End, cancellationToken),
                    change);
                if (exact.Length > ShapeCommitBytes)
                {
                    throw new ShapeTooLargeException();
                }
                size = exact.Length;
            }
            candidate.TransferTo(admission);
            if (change.Length > 0)
            {
                if (emitted++ > 0)
                {
                    changes.Write(","u8);
                }
                changes.Write(change);
            }
            count += next.Rows;
            covered = next.End;
            if (count >= ShapeRows || size >= ShapeBodyBytes)
            {
                break;
            }
        }

        if (covered == offset)
        {
            return (offset, head, null);
        }

        var body = ShapeEnvelope(shape, offset, covered, frontier, ShapeResume(shape.ResumeScope.Span, covered, cancellationToken),
            changes.WrittenSpan);
        PublishShapeLineage(shape.Handle, shape.Collection, shape.CollectionGeneration, shape.Incarnation);
        var insert = Command("INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ($handle, $offset, $end, $body, $at);");
        insert.Parameters.AddWithValue("$handle", shape.Handle);
        insert.Parameters.AddWithValue("$offset", offset);
        insert.Parameters.AddWithValue("$end", covered);
        insert.Parameters.AddWithValue("$body", body);
        var sealedAt = Member.Clock.UtcNow.ToUnixTimeMilliseconds();
        insert.Parameters.AddWithValue("$at", sealedAt);
        insert.ExecuteNonQuery();
        insert.Parameters.Clear();
        // The caller keeps the inserted bytes and their admission through commit, so publication never rereads the body.
        var retained = new ShapeMemory(memory, store.Tenant);
        admission.TransferTo(retained);
        return (covered, head, new ShapeSealedChunk(offset, covered, frontier, body, retained, sealedAt));
    }

    // The ids of this collection with a feed row above the frontier: their values at the head are not held elsewhere yet.
    private HashSet<string> ShapeUnsettled(string collection, long frontier, ShapeMemory admission)
    {
        var size = Command("SELECT coalesce(sum(octet_length(id)), 0), count(*) FROM _tb_feed WHERE seq > $frontier AND collection = $collection;", collection);
        size.Parameters.AddWithValue("$frontier", frontier);
        CountRead("shape");
        using (var reader = size.ExecuteReader())
        {
            reader.Read();
            admission.Reserve(checked(reader.GetInt64(0) * ShapeIdMemoryPerByte + reader.GetInt64(1) * 64));
        }

        var command = Command("SELECT id FROM _tb_feed WHERE seq > $frontier AND collection = $collection;", collection);
        command.Parameters.AddWithValue("$frontier", frontier);
        HashSet<string> ids = [with(StringComparer.Ordinal)];
        CountRead("shape");
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            ids.Add(rows.GetString(0));
        }
        return ids;
    }

    private bool ShapeBoundary(long offset)
    {
        var command = Command("""
            SELECT seq, commit_offset FROM _tb_feed WHERE seq >= $offset ORDER BY seq LIMIT 2;
            """);
        command.Parameters.AddWithValue("$offset", offset);
        CountRead("shape");
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }
        var seq = reader.GetInt64(0);
        var commit = reader.GetInt64(1);
        if (seq != offset)
        {
            // Retention can remove part of a commit, so its first row's predecessor is not a proven boundary.
            return offset == 0 && seq == 1;
        }
        return !reader.Read() || reader.GetInt64(1) != commit;
    }

    private (long Commit, long End, int Rows) ShapeCommit(long offset, long through, CancellationToken cancellationToken)
    {
        var command = Command("""
            SELECT seq, commit_offset, stamp, deleted, octet_length(collection), octet_length(id), origin
            FROM _tb_feed WHERE seq > $offset ORDER BY seq;
            """);
        command.Parameters.AddWithValue("$offset", offset);
        CountRead("shape");
        using var reader = command.ExecuteReader();
        var end = offset;
        var commit = -1L;
        var count = 0;
        var successor = false;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seq = reader.GetInt64(0);
            var at = reader.GetInt64(1);
            if (seq != end + 1)
            {
                throw new ShapeSealOffsetException();
            }
            if (commit >= 0 && at != commit)
            {
                if (at <= commit)
                {
                    throw new InvalidDataException("shape feed commits are not in local apply order");
                }
                successor = true;
                break;
            }
            if (seq > through || at <= 0)
            {
                throw new ShapeSealOffsetException();
            }
            if (reader.GetInt64(2) <= 0 || reader.GetInt64(3) is not (0 or 1) || reader.GetInt64(6) == 0
                || reader.GetInt64(4) is < 1 or > 63 || reader.GetInt64(5) is < 1 or > MaxIdBytes)
            {
                throw new InvalidDataException("a shape feed row has invalid event columns");
            }
            commit = at;
            end = seq;
            count++;
        }
        if (!successor && end != through)
        {
            throw new ShapeSealOffsetException();
        }
        return count > 0 ? (commit, end, count) : throw new ShapeSealOffsetException();
    }

    // Null when a document of the range is in `unsettled`.
    private byte[]? ShapeChange(ShapeSealDefinition shape, long start, long end, HashSet<string>? unsettled, CancellationToken cancellationToken)
    {
        var command = Command("""
            SELECT id, access_before, access_after FROM _tb_feed
            WHERE seq > $start AND seq <= $end AND collection = $collection;
            """, shape.Collection);
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);
        Dictionary<string, bool> rows = [with(StringComparer.Ordinal)];
        CountRead("shape");
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = reader.GetString(0);
                rows[id] = rows.GetValueOrDefault(id) || shape.OwnerAuthorization is null
                    || MatchesOwnerProof(reader.IsDBNull(1) ? null : reader.GetString(1), shape.OwnerAuthorization)
                    || MatchesOwnerProof(reader.IsDBNull(2) ? null : reader.GetString(2), shape.OwnerAuthorization);
            }
        }
        if (rows.Count == 0)
        {
            return [];
        }
        if (unsettled is not null && rows.Keys.Any(unsettled.Contains))
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = DocumentJson.MaxStoredDepth + 4 });
        writer.WriteStartObject();
        writer.WriteString("offset", ShapeNumber(end));
        writer.WriteStartArray("ops");
        var emitted = 0;
        foreach (var (id, owned) in rows.OrderBy(row => row.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = ReadShapeDocument(shape.Collection, id);
            if (document is not null)
            {
                DocumentJson.ValidateStored(document.Json.Span);
                if (shape.OwnerAuthorization is { } owner && new CollectionAccessRules(owner.Path, null, null).Subject(document.Json) != owner.Subject
                    || shape.Predicate is not null && !shape.Predicate(document))
                {
                    document = null;
                }
            }
            // A client where filter never grants permission to learn a tombstone's id.
            if (document is null && !owned)
            {
                continue;
            }
            emitted++;
            writer.WriteStartObject();
            writer.WriteString("op", document is null ? "delete" : "upsert");
            writer.WriteString("id", id);
            if (document is not null)
            {
                writer.WriteString("version", ETags.Version(Scope, shape.Collection, id, document.Version, document.Json));
                writer.WritePropertyName("document");
                writer.WriteRawValue(document.Json.Span, skipInputValidation: true);
            }
            writer.WriteEndObject();
            if (writer.BytesCommitted + writer.BytesPending > ShapeCommitBytes)
            {
                throw new ShapeTooLargeException();
            }
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return emitted == 0 ? [] : buffer.WrittenSpan.ToArray();
    }

    private static bool MatchesOwnerProof(string? json, DocumentOwnerProof owner) =>
        json is not null && JsonSerializer.Deserialize(json, ShapeJsonContext.Default.DocumentOwnerProof) == owner;

    // Project sizes before reading bodies; cover the candidate, growing output buffers and the persisted copy.
    private long ShapeChangeBytes(ShapeSealDefinition shape, long start, long end)
    {
        var collection = shape.Collection;
        var command = Command("""
            SELECT coalesce(sum(octet_length(id)), 0), count(*),
                coalesce(sum(coalesce(octet_length(access_before), 0) + coalesce(octet_length(access_after), 0)), 0) FROM _tb_feed
            WHERE seq > $start AND seq <= $end AND collection = $collection;
            """, collection);
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);
        long bytes;
        CountRead("shape");
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            bytes = checked(reader.GetInt64(0) * ShapeIdMemoryPerByte + reader.GetInt64(1) * 1024 + reader.GetInt64(2) * 8);
        }
        if (HasTable(collection, "shape"))
        {
            var bodies = TableCommand("""
                SELECT coalesce(sum(octet_length(doc)), 0), coalesce(max(octet_length(doc)), 0) FROM {0}
                WHERE id IN (SELECT id FROM _tb_feed WHERE seq > $start AND seq <= $end AND collection = $collection);
                """, collection);
            bodies.Parameters.AddWithValue("$start", start);
            bodies.Parameters.AddWithValue("$end", end);
            CountRead("shape");
            using var reader = bodies.ExecuteReader();
            reader.Read();
            if (reader.GetInt64(1) > DocumentJson.MaxBytes)
            {
                throw new InvalidDataException("a shape document exceeds the stored document bound");
            }
            if (shape.Predicate is null && reader.GetInt64(0) > ShapeCommitBytes)
            {
                throw new ShapeTooLargeException();
            }
            bytes = checked(bytes + Math.Min(reader.GetInt64(0), ShapeCommitBytes + MaxDocumentBytes) * 8);
        }
        return bytes;
    }

    private StoredDocument? ReadShapeDocument(string collection, string id)
    {
        if (!HasTable(collection, "shape"))
        {
            return null;
        }
        // Only visible documents belong in a shape; hidden uniqueness contenders become deletes.
        CountRead("shape");
        using var reader = TableCommand("""
            SELECT (SELECT stamp FROM _tb_state WHERE collection = $collection AND id = $id), doc FROM {0} WHERE id = $id;
            """, collection, id).ExecuteReader();
        return reader.Read() ? new StoredDocument(id, Version(collection, id, reader, 0), TursoSession.Bytes(reader, 1), Scope) : null;
    }

    private static byte[] ShapeEnvelope(ShapeSealDefinition shape, long offset, long covered, long head, string resume,
        ReadOnlySpan<byte> changes, bool upToDate = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("shape_id", shape.ShapeId);
        writer.WriteString("handle", shape.Handle);
        writer.WriteString("offset", ShapeNumber(offset));
        writer.WriteString("next_offset", ShapeNumber(covered));
        writer.WriteString("covered_offset", ShapeNumber(covered));
        writer.WriteString("values_at", ShapeNumber(head));
        writer.WriteBoolean("up_to_date", upToDate);
        writer.WriteString("cursor_resume", resume);
        writer.WriteStartArray("changes");
        writer.Flush();
        // Changes are already validated JSON objects, comma-separated; preserve their stored document bytes.
        buffer.Write(changes);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private string ShapeResume(ReadOnlySpan<byte> scope, long covered, CancellationToken cancellationToken)
    {
        // Each origin stops below its first row past covered: the persisted chain then holds covered rows only.
        var command = Command("""
            SELECT f.origin, max(f.stamp), min(f.stamp) FROM _tb_feed f
            LEFT JOIN (SELECT origin, min(stamp) AS stamp FROM _tb_feed WHERE seq > $covered GROUP BY origin) u ON u.origin = f.origin
            WHERE f.seq <= $covered AND (u.stamp IS NULL OR f.stamp < u.stamp) GROUP BY f.origin;
            """);
        command.Parameters.AddWithValue("$covered", covered);
        Dictionary<ReplicaId, Hlc> stamps = [];
        CountRead("shape");
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (stamps.Count == ChangeCursor.MaxOrigins)
                {
                    throw new InvalidDataException("a shape cursor exceeds the origin bound");
                }
                if (reader.GetInt64(0) == 0 || reader.GetInt64(2) <= 0)
                {
                    throw new InvalidDataException("a shape resume prefix has invalid event columns");
                }
                stamps.Add(new ReplicaId(unchecked((ulong)reader.GetInt64(0))), new Hlc(checked((ulong)reader.GetInt64(1))));
            }
        }
        var prefixes = stamps.ToDictionary(pair => pair.Key, pair =>
        {
            var floor = ShapeFloor(pair.Key);
            if (floor.Value > pair.Value.Value)
            {
                throw new InvalidDataException("a shape resume floor exceeds its origin head");
            }
            return PrefixThrough(pair.Key, floor, pair.Value, cancellationToken, "shape");
        });
        var cursor = new FeedCursor(stamps, FeedCursor.At(Member.Clock.UtcNow)) { Issuer = Member.Id, Prefixes = prefixes };
        var r8 = Base64Url.DecodeFromChars(ChangeCursor.Encode(cursor));
        var bytes = new byte[17 + r8.Length];
        bytes[0] = 1;
        scope.CopyTo(bytes.AsSpan(1));
        r8.CopyTo(bytes, 17);
        return Base64Url.EncodeToString(bytes);
    }

    private Hlc ShapeFloor(ReplicaId origin)
    {
        var command = Command("SELECT stamp FROM _tb_feed_floor WHERE origin = $origin;");
        command.Parameters.AddWithValue("$origin", unchecked((long)origin.Value));
        CountRead("shape");
        return command.ExecuteScalar() is long floor
            ? floor >= 0 ? new Hlc((ulong)floor) : throw new InvalidDataException("a shape resume floor is negative")
            : default;
    }

    private void ValidateShapeCapture(IMemoryBudget memory, CancellationToken cancellationToken)
    {
        CountRead("shape");
        if (Command("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'turso_cdc';").ExecuteScalar() is null)
        {
            return;
        }
        CountRead("shape");
        var head = Command("SELECT coalesce(max(change_id), 0) FROM turso_cdc;").ExecuteScalar() is long last ? last : 0;
        for (var after = 0L; after < head;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var boundary = Command("SELECT min(change_id) FROM turso_cdc WHERE change_id > $after AND change_type = 2;");
            boundary.Parameters.AddWithValue("$after", after);
            CountRead("shape");
            if (boundary.ExecuteScalar() is not long end)
            {
                throw new InvalidDataException("a captured transaction has no intact commit boundary");
            }
            var size = Command("""
                SELECT coalesce(sum(coalesce(octet_length(after), 0) * CASE WHEN table_name = '_tb_state' THEN $stateMemory ELSE $imageMemory END
                    + coalesce(octet_length(table_name), 0) * $imageMemory), 0), count(*),
                    coalesce(max(octet_length(table_name)), 0) FROM turso_cdc
                WHERE change_id > $after AND change_id <= $end;
                """);
            size.Parameters.AddWithValue("$after", after);
            size.Parameters.AddWithValue("$end", end);
            size.Parameters.AddWithValue("$stateMemory", ShapeCaptureStateMemoryPerByte);
            size.Parameters.AddWithValue("$imageMemory", ShapeCaptureMemoryPerByte);
            long bytes;
            CountRead("shape");
            using (var reader = size.ExecuteReader())
            {
                reader.Read();
                if (reader.GetInt64(2) > 128)
                {
                    throw new InvalidDataException("a captured table name exceeds the engine's name bound");
                }
                bytes = checked(reader.GetInt64(0) + reader.GetInt64(1) * 2048);
            }
            using var admission = memory.TryReserve(MemoryLane.Bulk, bytes, store.Tenant) ?? throw new QosMemoryException();
            using var tokenAdmission = new ShapeMemory(memory, store.Tenant);
            long tokenBytes = 0;
            var capture = new TailTransaction(store.IsSystem);
            var rows = Command("""
                SELECT change_type, table_name, after FROM turso_cdc
                WHERE change_id > $after AND change_id <= $end ORDER BY change_id;
                """);
            rows.Parameters.AddWithValue("$after", after);
            rows.Parameters.AddWithValue("$end", end);
            CountRead("shape");
            using (var reader = rows.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var type = reader.GetInt64(0);
                    // Match ReadChangeGroups' row-shape checks before reusing its state/image decoder.
                    if ((type == 2 && (!reader.IsDBNull(1) || !reader.IsDBNull(2))) || (type == -1 && !reader.IsDBNull(2)))
                    {
                        throw new InvalidDataException($"a captured change of type {type} has the shape of another type");
                    }
                    if (type == 2)
                    {
                        continue;
                    }
                    if (type is not (-1 or 0 or 1))
                    {
                        throw new InvalidDataException($"a captured change has change type {type}, which no capture writes");
                    }
                    if (type is 0 or 1 && reader.GetString(1) is var table && !ChangeLog.EngineTable(table)
                        && table is not ("_tb_feed" or "_tb_feed_floor"))
                    {
                        if (reader.IsDBNull(2))
                        {
                            throw new InvalidDataException("a captured insert or update has no after-image");
                        }
                        var image = TursoSession.Bytes(reader, 2);
                        var required = MeasureShapeRecord(image, table, cancellationToken);
                        if (required > tokenBytes)
                        {
                            tokenAdmission.Reserve(required - tokenBytes);
                            tokenBytes = required;
                        }
                        capture.Add(table, image, Member.Id);
                    }
                }
            }
            after = end;
        }
    }

    // The state row has nine columns; other captured client/metadata rows have at most nine.
    private static long MeasureShapeRecord(ReadOnlySpan<byte> image, string table, CancellationToken cancellationToken)
    {
        var size = ShapeRecordVarint(image, out var used);
        if (size < used || size > image.Length)
        {
            throw new InvalidDataException("a captured record header is out of range");
        }
        var header = image[used..(int)size];
        var body = image[(int)size..];
        ReadOnlySpan<byte> second = [];
        ReadOnlySpan<byte> third = [];
        var allText = true;
        var columns = 0;
        while (!header.IsEmpty)
        {
            if (++columns > 9)
            {
                throw new InvalidDataException("a captured record exceeds the engine column bound");
            }
            var type = ShapeRecordVarint(header, out var read);
            header = header[read..];
            var bytes = type switch
            {
                0 or 8 or 9 => 0,
                1 => 1,
                2 => 2,
                3 => 3,
                4 => 4,
                5 => 6,
                6 or 7 => 8,
                >= 12 => (type - 12) / 2,
                _ => throw new InvalidDataException("a captured record uses a reserved serial type"),
            };
            if (bytes > body.Length)
            {
                throw new InvalidDataException("a captured record value runs past its end");
            }
            allText &= type >= 13 && type % 2 == 1;
            if (allText && columns == 2)
            {
                second = body[..(int)bytes];
            }
            if (allText && columns == 3)
            {
                third = body[..(int)bytes];
            }
            body = body[(int)bytes..];
        }
        if (table == "_tb_state" && columns != 9)
        {
            throw new InvalidDataException("a captured state record has the wrong column count");
        }
        if (!allText || !(columns == 2 || columns == 3 && table == "documents"))
        {
            return 0;
        }
        // Match the sender's two-text branch even for a client collection named documents.
        var document = columns == 2 ? second : third;
        // Bookkeeping text is normally an id, but damaged state can reference it as a document in the sender.
        if (!(DatabaseName.TryParse(table, out _) || IsMetadata(table)))
        {
            return checked((long)document.Length * ShapeCaptureMemoryPerToken);
        }
        try
        {
            var json = new Utf8JsonReader(document, new JsonReaderOptions { MaxDepth = DocumentJson.MaxStoredDepth });
            long tokens = 0;
            while (json.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                tokens++;
            }
            return checked(tokens * ShapeCaptureMemoryPerToken);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("a captured document is not valid JSON", error);
        }
    }

    private static long ShapeRecordVarint(ReadOnlySpan<byte> bytes, out int read)
    {
        long value = 0;
        for (read = 0; read < 9 && read < bytes.Length; read++)
        {
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
        throw new InvalidDataException("a captured record varint is truncated");
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
        if (bytes is <= 0 or > ShapeCommitBytes)
        {
            throw new InvalidDataException("a sealed shape chunk exceeds its body bound");
        }
        var admission = memory.TryReserve(MemoryLane.Foreground, checked(bytes + 4096), store.Tenant) ?? throw new QosMemoryException();
        try
        {
            var command = Command("SELECT end_offset, body, sealed_at FROM _tb_shape_chunk WHERE handle = $handle AND offset = $offset;");
            command.Parameters.AddWithValue("$handle", handle);
            command.Parameters.AddWithValue("$offset", offset);
            // The payload read: the one SELECT a served chunk costs, counted apart from the size probe and other shape SQL.
            CountRead("shape_body");
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidDataException("a sealed shape chunk disappeared within a read transaction");
            }
            var end = reader.GetInt64(0);
            var body = TursoSession.Bytes(reader, 1);
            var valuesAt = ReadShapeValuesAt(body);
            return new ShapeSealedChunk(offset, end, valuesAt, body, admission, reader.GetInt64(2));
        }
        catch
        {
            admission.Dispose();
            throw;
        }
    }, cancellationToken);

    private static string ShapeNumber(long offset) => offset.ToString(CultureInfo.InvariantCulture);

    private static long ReadShapeValuesAt(ReadOnlySpan<byte> body)
    {
        var reader = new Utf8JsonReader(body);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("values_at"u8) && reader.Read()
                && reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length <= 19
                && Utf8Parser.TryParse(reader.ValueSpan, out long head, out var consumed) && consumed == reader.ValueSpan.Length && head >= 0)
            {
                return head;
            }
        }
        throw new InvalidDataException("a sealed shape chunk has no materialization head");
    }

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
