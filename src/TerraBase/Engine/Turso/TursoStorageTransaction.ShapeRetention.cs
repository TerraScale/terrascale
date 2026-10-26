using TerraBase.Features.Shapes;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

/// <summary>
/// Shape retention (FR-16): bounded deletes of a handle's expired or unreachable sealed chunks and superseded snapshots.
/// A handle's floor is the highest offset retention removed, kept in a sentinel <c>_tb_shape_chunk</c> row under
/// <c>~floor:</c> plus the handle (offset 0, empty body, sealed_at 0), raised before the deletes it covers; a canonical
/// <c>h1-</c> handle never starts with <c>~</c>. After a maintenance pass the floor also covers the changes feed's holes.
/// Sealers still check range density after concurrent feed GC. Retention writes this derived data uncaptured.
/// </summary>
internal sealed partial class TursoStorageTransaction : IShapeRetentionTransaction
{
    private const string ShapeFloorPrefix = "~floor:";

    public ValueTask<long> ReadShapeFloorAsync(string handle, CancellationToken cancellationToken) =>
        RunAsync(() => ShapeFloor(handle), cancellationToken);

    public async ValueTask<int> TrimShapeAsync(string handle, long before, int maxRows, long maxBytes, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        return await RunAsync(() => TrimShape(handle, before, maxRows, maxBytes, cancellationToken).Rows, cancellationToken, mutation: true)
            .ConfigureAwait(false);
    }

    // Worker-only: one bounded trim of a current handle. Raised is the floor it left when that is above the stored one, else 0,
    // so a floor raised by the feed alone is reported too; More when a candidate did not fit the byte budget.
    private (int Rows, long Bytes, long Raised, bool More) TrimShape(string handle, long before, int maxRows, long maxBytes, CancellationToken cancellationToken)
    {
        var stored = StoredShapeFloor(handle);
        var floor = Math.Max(stored, FeedHoleFloor());
        if (floor > 0)
        {
            RaiseShapeFloor(handle, floor);
        }
        var chunks = Command("""
            SELECT rowid, end_offset, octet_length(body) FROM _tb_shape_chunk
            WHERE handle = $handle AND (sealed_at < $before OR offset < $floor) ORDER BY offset LIMIT $limit;
            """);
        chunks.Parameters.AddWithValue("$handle", handle);
        chunks.Parameters.AddWithValue("$before", before);
        chunks.Parameters.AddWithValue("$floor", floor);
        chunks.Parameters.AddWithValue("$limit", (long)maxRows);
        var (rows, bytes, end, more) = ShapeBudget(chunks, maxBytes);
        if (rows.Count > 0 && end > floor)
        {
            // The floor rises in the same transaction, before the rows it now hides go.
            RaiseShapeFloor(handle, end);
            floor = end;
        }

        var raised = floor > stored ? floor : 0;
        var deleted = DeleteShapeRows("_tb_shape_chunk", rows, cancellationToken);
        if (deleted == maxRows)
        {
            return (deleted, bytes, raised, more);
        }

        // The newest complete snapshot keeps every page, however old: it is the only full read a refetch can start from,
        // and an incomplete newer one being sealed does not replace it.
        var snapshots = Command("""
            SELECT rowid, offset, octet_length(body) FROM _tb_shape_snapshot
            WHERE handle = $handle AND offset < $floor AND sealed_at < $before
            AND offset < (SELECT max(offset) FROM _tb_shape_snapshot WHERE handle = $handle AND next_id IS NULL)
            ORDER BY offset, page LIMIT $limit;
            """);
        snapshots.Parameters.AddWithValue("$handle", handle);
        snapshots.Parameters.AddWithValue("$floor", floor);
        snapshots.Parameters.AddWithValue("$before", before);
        snapshots.Parameters.AddWithValue("$limit", (long)(maxRows - deleted));
        (rows, var snapshotBytes, _, var snapshotMore) = ShapeBudget(snapshots, maxBytes - bytes);
        return (deleted + DeleteShapeRows("_tb_shape_snapshot", rows, cancellationToken), bytes + snapshotBytes, raised, more || snapshotMore);
    }

    public async ValueTask<int> DropShapeAsync(string handle, int maxRows, long maxBytes, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        return await RunAsync(() => DropShape(handle, maxRows, maxBytes, cancellationToken).Rows, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Worker-only: one bounded batch of a retired handle's rows and the body bytes they held.
    private (int Rows, long Bytes) DropShape(string handle, int maxRows, long maxBytes, CancellationToken cancellationToken)
    {
        var chunks = Command("SELECT rowid, end_offset, octet_length(body) FROM _tb_shape_chunk WHERE handle = $handle ORDER BY offset LIMIT $limit;");
        chunks.Parameters.AddWithValue("$handle", handle);
        chunks.Parameters.AddWithValue("$limit", (long)maxRows);
        var (rows, bytes, _, _) = ShapeBudget(chunks, maxBytes);
        var deleted = DeleteShapeRows("_tb_shape_chunk", rows, cancellationToken);

        var snapshots = Command("SELECT rowid, offset, octet_length(body) FROM _tb_shape_snapshot WHERE handle = $handle ORDER BY offset, page LIMIT $limit;");
        snapshots.Parameters.AddWithValue("$handle", handle);
        snapshots.Parameters.AddWithValue("$limit", (long)(maxRows - deleted));
        (rows, var snapshotBytes, _, _) = ShapeBudget(snapshots, maxBytes - bytes);
        deleted += DeleteShapeRows("_tb_shape_snapshot", rows, cancellationToken);
        bytes += snapshotBytes;

        if (deleted == maxRows)
        {
            return (deleted, bytes);
        }

        // The sentinel outlives the real rows until the last batch, so a partial drop still refuses old offsets; the
        // lineage record outlives the floor, so the handle stays classifiable until nothing of it is left.
        var forget = Command("""
            SELECT rowid, end_offset, octet_length(body) FROM _tb_shape_chunk WHERE handle = $sentinel AND offset = 0
            AND NOT EXISTS (SELECT 1 FROM _tb_shape_chunk WHERE handle = $handle)
            AND NOT EXISTS (SELECT 1 FROM _tb_shape_snapshot WHERE handle = $handle);
            """);
        forget.Parameters.AddWithValue("$sentinel", ShapeFloorPrefix + handle);
        forget.Parameters.AddWithValue("$handle", handle);
        if (!DropShapeRecord(forget, maxBytes, ref deleted, ref bytes, cancellationToken) || deleted == maxRows)
        {
            return (deleted, bytes);
        }

        var lineage = Command("""
            SELECT rowid, end_offset, octet_length(body) FROM _tb_shape_chunk WHERE handle = $lineage AND offset = 0
            AND NOT EXISTS (SELECT 1 FROM _tb_shape_chunk WHERE handle IN ($handle, $sentinel))
            AND NOT EXISTS (SELECT 1 FROM _tb_shape_snapshot WHERE handle = $handle);
            """);
        lineage.Parameters.AddWithValue("$lineage", ShapeLineagePrefix + handle);
        lineage.Parameters.AddWithValue("$sentinel", ShapeFloorPrefix + handle);
        lineage.Parameters.AddWithValue("$handle", handle);
        _ = DropShapeRecord(lineage, maxBytes, ref deleted, ref bytes, cancellationToken);
        return (deleted, bytes);
    }

    // Worker-only: deletes the floor or lineage row the candidate names, if any, and charges its body; false when it is left
    // for the next batch because the budget is spent or it does not fit. Reads its size only; an oversized one faults the batch.
    private bool DropShapeRecord(SqliteCommand candidate, long maxBytes, ref int deleted, ref long bytes, CancellationToken cancellationToken)
    {
        var (rows, size, _, left) = ShapeBudget(candidate, bytes < maxBytes ? maxBytes - bytes : -1);
        deleted += DeleteShapeRows("_tb_shape_chunk", rows, cancellationToken);
        bytes += size;
        return !left;
    }

    // Worker-only: the handle's sentinel floor, or the feed's when higher.
    private long ShapeFloor(string handle)
    {
        var floor = StoredShapeFloor(handle);
        CountRead("shape");
        var counter = Command("SELECT seq FROM _tb_feed_sequence WHERE id = 1;").ExecuteScalar() is long seq ? seq : 0;
        var head = Command("SELECT 1 FROM _tb_feed WHERE seq = $seq;");
        head.Parameters.AddWithValue("$seq", counter);
        if (counter > 0)
        {
            CountRead("shape");
            if (head.ExecuteScalar() is null)
            {
                return Math.Max(floor, counter);
            }
        }

        CountRead("shape");
        var first = Command("SELECT min(seq) FROM _tb_feed;").ExecuteScalar();
        return Math.Max(floor, first is long start ? start - 1 : counter);
    }

    // Worker-only: the handle's sentinel floor alone.
    private long StoredShapeFloor(string handle)
    {
        var sentinel = Command("SELECT max(end_offset) FROM _tb_shape_chunk WHERE handle = $sentinel AND offset = 0;");
        sentinel.Parameters.AddWithValue("$sentinel", ShapeFloorPrefix + handle);
        CountRead("shape");
        return sentinel.ExecuteScalar() is long end ? end : 0;
    }

    // Worker-only: the highest feed sequence no longer retained. A missing head is the counter itself; otherwise the
    // highest retained seq whose predecessor is gone names the hole below it (a trimmed prefix included).
    private long FeedHoleFloor()
    {
        CountRead("shape");
        var counter = Command("SELECT seq FROM _tb_feed_sequence WHERE id = 1;").ExecuteScalar() is long seq ? seq : 0;
        var head = Command("SELECT 1 FROM _tb_feed WHERE seq = $seq;");
        head.Parameters.AddWithValue("$seq", counter);
        if (counter > 0)
        {
            CountRead("shape");
            if (head.ExecuteScalar() is null)
            {
                return counter;
            }
        }

        // shortcut: maintenance scans the seq index for interior holes; persist the floor in feed GC when this scan costs
        // too much. Request floor reads use the handle's persisted floor and indexed prefix/head lookups instead.
        var hole = Command("""
            SELECT seq FROM _tb_feed f WHERE seq > 1 AND NOT EXISTS (SELECT 1 FROM _tb_feed p WHERE p.seq = f.seq - 1)
            ORDER BY seq DESC LIMIT 1;
            """);
        CountRead("shape");
        return hole.ExecuteScalar() is long above ? above - 1 : 0;
    }

    // Worker-only: the candidates' longest prefix within maxBytes, its body bytes, its highest second column, and whether a
    // candidate did not fit. Reads row metadata only; bodies stay on disk.
    private (List<long> Rows, long Bytes, long End, bool More) ShapeBudget(SqliteCommand candidates, long maxBytes)
    {
        var rows = new List<long>();
        long bytes = 0;
        long end = 0;
        CountRead("shape");
        using var reader = candidates.ExecuteReader();
        while (reader.Read())
        {
            var size = reader.GetInt64(2);
            if (size > ShapeRetention.BatchBytes)
            {
                throw new InvalidDataException("A sealed shape body exceeds the 8 MiB object bound.");
            }

            if (bytes + size > maxBytes)
            {
                return (rows, bytes, end, true);
            }

            rows.Add(reader.GetInt64(0));
            bytes += size;
            end = Math.Max(end, reader.GetInt64(1));
        }

        return (rows, bytes, end, false);
    }

    // Worker-only: a failed or cancelled delete faults the mutation, and the transaction rolls back with the floor.
    private int DeleteShapeRows(string table, List<long> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var delete = Command($"DELETE FROM {table} WHERE rowid = $rowid;");
            delete.Parameters.AddWithValue("$rowid", row);
            delete.ExecuteNonQuery();
        }

        return rows.Count;
    }

    // Worker-only; never lowers the floor.
    private void RaiseShapeFloor(string handle, long end)
    {
        var sentinel = ShapeFloorPrefix + handle;
        var update = Command("UPDATE _tb_shape_chunk SET end_offset = max(end_offset, $end) WHERE handle = $sentinel AND offset = 0;");
        update.Parameters.AddWithValue("$sentinel", sentinel);
        update.Parameters.AddWithValue("$end", end);
        if (update.ExecuteNonQuery() == 0)
        {
            var insert = Command("INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ($sentinel, 0, $end, X'', 0);");
            insert.Parameters.AddWithValue("$sentinel", sentinel);
            insert.Parameters.AddWithValue("$end", end);
            insert.ExecuteNonQuery();
        }
    }
}
