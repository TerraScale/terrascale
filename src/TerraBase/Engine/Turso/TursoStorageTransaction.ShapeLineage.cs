using System.Buffers;
using System.Globalization;
using System.Text;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Shapes;
using TerraBase.Primitives;

namespace TerraBase.Engine.Turso;

/// <summary>
/// Durable shape lineage (#277, docs/terrabase/shape-sweep.md): each handle with objects keeps a sentinel <c>_tb_shape_chunk</c>
/// row under <c>~lineage:</c> plus the handle (offset 0, end_offset 0, sealed_at 0), whose body is the ASCII text
/// <c>1 incarnation replica generation collection</c>, at most <see cref="LineageBodyBytes"/> bytes. Both object writers publish
/// it in the transaction that inserts their object, after reading the lineage they write under in that transaction; the sweep
/// reads it the same way to tell a retired handle from a current one. Every other reader keys rows by an exact canonical
/// <c>h1-</c> handle, which never starts with <c>~</c>; copies skip the table.
/// </summary>
internal sealed partial class TursoStorageTransaction : IShapeSweepTransaction
{
    private const string ShapeLineagePrefix = "~lineage:";
    private const int LineageBodyBytes = 256;
    // Keys longer than this are not handles: listed truncated, past every key that shares the prefix, and never classified.
    private const int ShapeKeyChars = 64;
    // Sweep reads are counted apart from the reads that serve and seal shapes.
    private const string Sweep = "shape_sweep";
    private static readonly SearchValues<char> ShapeHandleHex = SearchValues.Create("0123456789abcdef");

    // Each object writer, before inserting: the lineage it names must be the current one, read in this writer transaction.
    // The replica is this process's own (Member.Id), fixed for its lifetime, so only the incarnation comes from the caller.
    private void PublishShapeLineage(string handle, string collection, string generation, Ulid incarnation)
    {
        var expected = ShapeLineageBody(incarnation, Member.Id, generation, collection);
        if (CurrentShapeLineage(collection, "shape") is not { } current || !current.AsSpan().SequenceEqual(expected))
        {
            throw new ShapeSealOffsetException();
        }

        var lineage = Command("SELECT body FROM _tb_shape_chunk WHERE handle = $key AND offset = 0 AND octet_length(body) <= $max;");
        lineage.Parameters.AddWithValue("$key", ShapeLineagePrefix + handle);
        lineage.Parameters.AddWithValue("$max", LineageBodyBytes);
        CountRead("shape");
        if (lineage.ExecuteScalar() is byte[] stored)
        {
            if (!stored.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidDataException("a shape handle's lineage record names another lineage");
            }

            return;
        }

        // An oversized record makes the insert fail on the unique key, so it is never replaced.
        var insert = Command("INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ($key, 0, 0, $body, 0);");
        insert.Parameters.AddWithValue("$key", ShapeLineagePrefix + handle);
        insert.Parameters.AddWithValue("$body", expected);
        insert.ExecuteNonQuery();
    }

    // The lineage a handle of this collection is computed from now; null once the collection's deletion is visible.
    private byte[]? CurrentShapeLineage(string collection, string path)
    {
        CountRead(path);
        if (Command("SELECT entity_id FROM collections WHERE name = $collection;", collection).ExecuteScalar() is not string generation)
        {
            return null;
        }

        CountRead(path);
        // An unreadable incarnation proves nothing about any handle: fail rather than call every lineage retired.
        return Command(TursoStatements.Incarnation).ExecuteScalar() is string incarnation && EntityIds.IsValid(incarnation)
            ? ShapeLineageBody(Ulid.Parse(incarnation), Member.Id, generation, collection)
            : throw new InvalidDataException("the database has no valid incarnation");
    }

    private static byte[] ShapeLineageBody(Ulid incarnation, ReplicaId replica, string generation, string collection) =>
        Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"1 {incarnation} {replica.Value} {generation} {collection}"));

    public ValueTask<IReadOnlyList<string>> ListShapeHandlesAsync(string? after, int limit, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<string>>(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            List<string> handles = [];
            var cursor = after ?? "";
            while (handles.Count < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? next = null;
                // Canonical keys, floor-only and lineage-only handles, other client-range keys, and snapshot keys.
                foreach (var candidate in (ReadOnlySpan<string?>)[
                    NextShapeKey("_tb_shape_chunk", "", cursor, "~"), NextShapeKey("_tb_shape_chunk", ShapeFloorPrefix, cursor, "~floor;"),
                    NextShapeKey("_tb_shape_chunk", ShapeLineagePrefix, cursor, "~lineage;"),
                    NextShapeKey("_tb_shape_chunk", "", string.CompareOrdinal(cursor, "\u007f") > 0 ? cursor : "\u007f", null),
                    NextShapeKey("_tb_shape_snapshot", "", cursor, null)])
                {
                    if (candidate is not null && (next is null || string.CompareOrdinal(candidate, next) < 0))
                    {
                        next = candidate;
                    }
                }

                // SQLite orders keys by UTF-8 bytes, this loop by UTF-16 code units: stop rather than repeat a key.
                if (next is null || string.CompareOrdinal(next, cursor) <= 0)
                {
                    break;
                }

                handles.Add(next);
                cursor = next;
            }

            return handles;
        }, cancellationToken);

    // One indexed seek: the least key above prefix + after, without its prefix. No body is read.
    private string? NextShapeKey(string table, string prefix, string after, string? below)
    {
        var command = Command(below is null
            ? $"SELECT substr(handle, $skip, $chars), length(handle) FROM {table} WHERE handle > $low ORDER BY handle LIMIT 1;"
            : $"SELECT substr(handle, $skip, $chars), length(handle) FROM {table} WHERE handle > $low AND handle < $high ORDER BY handle LIMIT 1;");
        command.Parameters.AddWithValue("$skip", prefix.Length + 1);
        command.Parameters.AddWithValue("$chars", ShapeKeyChars);
        command.Parameters.AddWithValue("$low", prefix + after);
        if (below is not null)
        {
            command.Parameters.AddWithValue("$high", below);
        }

        CountRead(Sweep);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var key = reader.GetString(0);
        return reader.GetInt64(1) - prefix.Length > ShapeKeyChars ? key + "\U0010FFFF" : key;
    }

    public async ValueTask<ShapeSweepStep> SweepShapeHandleAsync(string handle, long before, int maxRows, long maxBytes, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        return await RunAsync(() =>
        {
            // Classified and deleted in this one writer transaction: a writer that publishes after it sees the same lineage.
            var state = ClassifyShapeHandle(handle);
            if (state == ShapeHandleState.Current)
            {
                // Ordinary retention, as a request runs it: a batch that deleted rows, or stopped at the byte budget, continues.
                var (rows, bytes, floor, more) = TrimShape(handle, before, maxRows, maxBytes, cancellationToken);
                return new ShapeSweepStep(state, rows, bytes, floor, rows > 0 || more);
            }

            if (state != ShapeHandleState.Retired)
            {
                return new ShapeSweepStep(state, 0, 0, 0, false);
            }

            var (deleted, dropped) = DropShape(handle, maxRows, maxBytes, cancellationToken);
            var remains = Command("""
                SELECT EXISTS (SELECT 1 FROM _tb_shape_chunk WHERE handle IN ($handle, $floor, $lineage))
                    OR EXISTS (SELECT 1 FROM _tb_shape_snapshot WHERE handle = $handle);
                """);
            remains.Parameters.AddWithValue("$handle", handle);
            remains.Parameters.AddWithValue("$floor", ShapeFloorPrefix + handle);
            remains.Parameters.AddWithValue("$lineage", ShapeLineagePrefix + handle);
            CountRead(Sweep);
            return new ShapeSweepStep(state, deleted, dropped, 0, (long)remains.ExecuteScalar()! != 0);
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Retired only when a valid record names a lineage that is no longer current. Absent, damaged or unknown is protected.
    private ShapeHandleState ClassifyShapeHandle(string handle)
    {
        if (handle.Length != 19 || !handle.StartsWith("h1-", StringComparison.Ordinal) || handle.AsSpan(3).ContainsAnyExcept(ShapeHandleHex))
        {
            return ShapeHandleState.Unknown;
        }

        var command = Command("SELECT CASE WHEN octet_length(body) <= $max THEN body END, end_offset, sealed_at FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
        command.Parameters.AddWithValue("$key", ShapeLineagePrefix + handle);
        command.Parameters.AddWithValue("$max", LineageBodyBytes);
        byte[] body;
        CountRead(Sweep);
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return ShapeHandleState.Unclassified;
            }

            if (reader.IsDBNull(0) || reader.GetInt64(1) != 0 || reader.GetInt64(2) != 0)
            {
                return ShapeHandleState.Malformed;
            }

            body = TursoSession.Bytes(reader, 0);
        }

        if (body.AsSpan().ContainsAnyExceptInRange((byte)' ', (byte)'~') || Encoding.ASCII.GetString(body).Split(' ') is not ["1", var incarnation, var replica, var generation, var collection]
            || !EntityIds.IsValid(incarnation) || !ulong.TryParse(replica, NumberStyles.None, CultureInfo.InvariantCulture, out var replicaId)
            || replicaId.ToString(CultureInfo.InvariantCulture) != replica || !EntityIds.IsValid(generation) || !DatabaseName.TryParse(collection, out _))
        {
            return ShapeHandleState.Malformed;
        }

        return CurrentShapeLineage(collection, Sweep) is { } current && current.AsSpan().SequenceEqual(body) ? ShapeHandleState.Current : ShapeHandleState.Retired;
    }
}
