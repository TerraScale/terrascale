using TerraBase.Features.Shapes;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

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

    private (int Rows, long Bytes, long Raised, bool More) TrimShape(string handle, long before, int maxRows, long maxBytes, CancellationToken cancellationToken)
    {
        var stored = StoredShapeFloor(handle);
        var expired = Command("SELECT coalesce(max(change_id), 0) FROM turso_cdc WHERE change_type = 2 AND change_time < $before;");
        expired.Parameters.AddWithValue("$before", before / 1000);
        var floor = Math.Max(stored, (long)expired.ExecuteScalar()!);
        if (floor > stored)
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
            RaiseShapeFloor(handle, end);
            floor = end;
        }

        var raised = floor > stored ? floor : 0;
        var deleted = DeleteShapeRows("_tb_shape_chunk", rows, cancellationToken);
        if (deleted == maxRows)
        {
            return (deleted, bytes, raised, more);
        }

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
        deleted += DeleteShapeRows("_tb_shape_snapshot", rows, cancellationToken);
        var backup = store.BackupAnchor ?? 0;
        var rewrite = store.RewriteAnchor;
        _ = Trim(session.Connection, transaction, rewrite < 0 ? backup : Math.Min(backup, rewrite));
        store.ObserveCdcSize(session.Connection, transaction);
        return (deleted, bytes + snapshotBytes, raised, more || snapshotMore);
    }

    public async ValueTask<int> DropShapeAsync(string handle, int maxRows, long maxBytes, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        return await RunAsync(() => DropShape(handle, maxRows, maxBytes, cancellationToken).Rows, cancellationToken, mutation: true).ConfigureAwait(false);
    }

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

    private bool DropShapeRecord(SqliteCommand candidate, long maxBytes, ref int deleted, ref long bytes, CancellationToken cancellationToken)
    {
        var (rows, size, _, left) = ShapeBudget(candidate, bytes < maxBytes ? maxBytes - bytes : -1);
        deleted += DeleteShapeRows("_tb_shape_chunk", rows, cancellationToken);
        bytes += size;
        return !left;
    }

    private long ShapeFloor(string handle) => StoredShapeFloor(handle);

    private long StoredShapeFloor(string handle)
    {
        var sentinel = Command("SELECT max(end_offset) FROM _tb_shape_chunk WHERE handle = $sentinel AND offset = 0;");
        sentinel.Parameters.AddWithValue("$sentinel", ShapeFloorPrefix + handle);
        CountRead("shape");
        return sentinel.ExecuteScalar() is long end ? end : 0;
    }

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
