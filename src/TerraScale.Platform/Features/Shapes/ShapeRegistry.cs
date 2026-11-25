namespace TerraScale.Platform.Features.Shapes;

public sealed record ShapeChunks(IReadOnlyList<ShapeChunkInfo> Items);

public sealed record ActiveShapeDatabases(IReadOnlyList<Ulid> Items);

public sealed record ClaimedShapes(IReadOnlyList<ShapeRecord> Items);

public sealed record ShapePage(IReadOnlyList<ShapeRecord> Items);

public sealed record ShapeObjectDeletion(string Id, string Handle, string Name, bool Referenced);

public sealed record ShapeObjectDeletions(IReadOnlyList<ShapeObjectDeletion> Items);

public sealed record ShapeDeletionsUpdated;

public readonly union RegisterShapeOutcome(ShapeRecord, PlatformNotFound, PlatformUnavailable);

public readonly union ReadShapeOutcome(ShapeRecord, PlatformNotFound, PlatformUnavailable);

public readonly union ListShapeChunksOutcome(ShapeChunks, PlatformUnavailable);

public readonly union ReadShapeChunkOutcome(ShapeChunkInfo, PlatformNotFound, PlatformUnavailable);

public readonly union CommitShapeChunkOutcome(bool, PlatformUnavailable);

public readonly union TrimShapeChunksOutcome(ShapeChunks, PlatformUnavailable);

public readonly union ActiveShapeDatabasesOutcome(ActiveShapeDatabases, PlatformUnavailable);

public readonly union ClaimShapesOutcome(ClaimedShapes, PlatformUnavailable);

public readonly union RetireShapeOutcome(ShapeRetirement, PlatformUnavailable);

public readonly union PageShapesOutcome(ShapePage, PlatformUnavailable);

public readonly union DueShapeDeletionsOutcome(ShapeObjectDeletions, PlatformUnavailable);

public readonly union UpdateShapeDeletionsOutcome(ShapeDeletionsUpdated, PlatformUnavailable);

public sealed class ShapeRegistry(PlatformStore store, TimeProvider timeProvider)
{
    public const long RetainedChunks = 128;
    public const string HeadPointer = "head.json";
    public static readonly TimeSpan ActiveFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan ObjectSettle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetiredDatabaseFence = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetiredDatabaseKept = TimeSpan.FromDays(1);

    private const string RecordColumns =
        "handle, shape_id, organization_id, project_id, database_id, project_route, database_route, table_name, where_clause, columns, viewer_key, head, head_digest";

    private const string ChunkColumns = "chunk_offset, object, sha256, size, epoch, digest";

    public async ValueTask<RegisterShapeOutcome> EnsureAsync(ShapeRecord shape, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UnixMs();
        var handle = SqlValues.Text(shape.Handle);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(
                "INSERT INTO platform_shapes (handle, shape_id, organization_id, project_id, database_id, project_route, database_route, table_name, "
                + "where_clause, columns, viewer_key, head, head_digest, sealer, owner, lease_until, last_read_at, created_at) "
                + "SELECT ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, '', '', '', 0, ?, ? "
                + "WHERE NOT EXISTS (SELECT 1 FROM platform_shape_database_retirements WHERE database_id = ? AND requested_at > ?) "
                + "ON CONFLICT (handle) DO UPDATE SET last_read_at = excluded.last_read_at, project_route = excluded.project_route, database_route = excluded.database_route",
                handle,
                SqlValues.Text(shape.ShapeId),
                SqlValues.Text(shape.Organization.ToString()),
                SqlValues.Text(shape.Project.ToString()),
                SqlValues.Text(shape.Database.ToString()),
                SqlValues.Text(shape.ProjectRoute),
                SqlValues.Text(shape.DatabaseRoute),
                SqlValues.Text(shape.Table),
                SqlValues.OptionalText(shape.Where),
                SqlValues.OptionalText(shape.Columns),
                SqlValues.Text(shape.ViewerKey),
                SqlValues.Number(now),
                SqlValues.Number(now),
                SqlValues.Text(shape.Database.ToString()),
                SqlValues.Number(now - (long)RetiredDatabaseFence.TotalMilliseconds)),
            SqlValues.Statement($"SELECT {RecordColumns} FROM platform_shapes WHERE handle = ?", handle),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => results[1].FirstRow() is { } row
                ? Record(row)
                : new PlatformNotFound("the database was deleted", "database_not_found"),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ReadShapeOutcome> ReadAsync(string handle, CancellationToken cancellationToken)
    {
        var outcome = await store.ReadAsync(SqlValues.Statement($"SELECT {RecordColumns} FROM platform_shapes WHERE handle = ?", SqlValues.Text(handle)), cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => results[0].FirstRow() is { } row
                ? Record(row)
                : new PlatformNotFound("the shape is not registered", "shape_not_found"),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ListShapeChunksOutcome> ChunksAfterAsync(
        string handle,
        long after,
        int limit,
        CancellationToken cancellationToken)
    {
        var outcome = await store.ReadAsync(
            SqlValues.Statement(
                $"SELECT {ChunkColumns} FROM platform_shape_chunks WHERE handle = ? AND chunk_offset > ? ORDER BY chunk_offset LIMIT ?",
                SqlValues.Text(handle),
                SqlValues.Number(after),
                SqlValues.Number(limit)),
            cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => Chunks(results[0]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ReadShapeChunkOutcome> ChunkAsync(string handle, long offset, CancellationToken cancellationToken)
    {
        var outcome = await store.ReadAsync(
            SqlValues.Statement($"SELECT {ChunkColumns} FROM platform_shape_chunks WHERE handle = ? AND chunk_offset = ?", SqlValues.Text(handle), SqlValues.Number(offset)),
            cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => results[0].FirstRow() is { } row
                ? Chunk(row)
                : new PlatformNotFound("the shape chunk is no longer retained", "shape_chunk_missing"),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<CommitShapeChunkOutcome> CommitAsync(ShapeRecord shape, ShapeChunkInfo chunk, long rows, string sealer, CancellationToken cancellationToken)
    {
        var handle = SqlValues.Text(shape.Handle);
        var now = timeProvider.GetUtcNow();
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(
                "UPDATE platform_shapes SET head = ?, head_digest = ?, sealer = ? WHERE handle = ? AND head = ?",
                SqlValues.Number(chunk.Offset),
                SqlValues.Text(chunk.Digest),
                SqlValues.Text(sealer),
                handle,
                SqlValues.Number(shape.Head)),
            SqlValues.Statement(
                "INSERT INTO platform_shape_chunks (handle, chunk_offset, object, sha256, size, epoch, digest, row_count, created_at) "
                + "SELECT ?, ?, ?, ?, ?, ?, ?, ?, ? WHERE (SELECT sealer FROM platform_shapes WHERE handle = ?) = ? "
                + "ON CONFLICT (handle, chunk_offset) DO NOTHING",
                handle,
                SqlValues.Number(chunk.Offset),
                SqlValues.Text(chunk.Name),
                SqlValues.Text(chunk.Sha256),
                SqlValues.Number(chunk.Size),
                SqlValues.Number(chunk.Epoch),
                SqlValues.Text(chunk.Digest),
                SqlValues.Number(rows),
                SqlValues.Number(now.UnixMs()),
                handle,
                SqlValues.Text(sealer)),
            SqlValues.Statement(
                "INSERT OR IGNORE INTO platform_shape_object_deletions (id, handle, name, due_at) SELECT ?, ?, ?, ? "
                + "WHERE (SELECT sealer FROM platform_shapes WHERE handle = ?) = ?",
                SqlValues.Text(Guard(shape.Handle, HeadPointer, sealer)),
                handle,
                SqlValues.Text(HeadPointer),
                SqlValues.Number((now + ObjectSettle).UnixMs()),
                handle,
                SqlValues.Text(sealer)),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Strong, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => results[0].RowsAffected == 1 && results[1].RowsAffected == 1,
            PlatformConflict => false,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<TrimShapeChunksOutcome> TrimAsync(string handle, long head, string sealer, CancellationToken cancellationToken)
    {
        var floor = head - RetainedChunks;
        var key = SqlValues.Text(handle);
        var limit = SqlValues.Number(floor);
        List<SqlHttpStatement> statements =
        [
            SqlValues.Statement(
                "DELETE FROM platform_shape_object_deletions WHERE id = ? AND EXISTS (SELECT 1 FROM platform_shapes WHERE handle = ?)",
                SqlValues.Text(Guard(handle, HeadPointer, sealer)),
                key),
        ];
        if (floor > 0)
        {
            statements.Add(SqlValues.Statement($"SELECT {ChunkColumns} FROM platform_shape_chunks WHERE handle = ? AND chunk_offset <= ?", key, limit));
            statements.Add(SqlValues.Statement(
                "INSERT OR IGNORE INTO platform_shape_object_deletions (id, handle, name, due_at) "
                + "SELECT handle || '/' || object, handle, object, ? FROM platform_shape_chunks WHERE handle = ? AND chunk_offset <= ?",
                SqlValues.Number(timeProvider.GetUtcNow().UnixMs()),
                key,
                limit));
            statements.Add(SqlValues.Statement("DELETE FROM platform_shape_chunks WHERE handle = ? AND chunk_offset <= ?", key, limit));
        }

        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => results.Count > 1 ? Chunks(results[1]) : new ShapeChunks([]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ActiveShapeDatabasesOutcome> ActiveDatabasesAsync(CancellationToken cancellationToken)
    {
        var outcome = await store.ReadAsync(
            SqlValues.Statement(
                "SELECT DISTINCT database_id FROM platform_shapes WHERE last_read_at > ?",
                SqlValues.Number((timeProvider.GetUtcNow() - ActiveFor).UnixMs())),
            cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => new ActiveShapeDatabases([.. results[0].RowsOf().Select(static row => Ulid.Parse(row.Text(0)))]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ClaimShapesOutcome> ClaimAsync(string owner, TimeSpan lease, int limit, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var ownerValue = SqlValues.Text(owner);
        var until = SqlValues.Number((now + lease).UnixMs());
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(
                "UPDATE platform_shapes SET owner = ?, lease_until = ? WHERE handle IN (SELECT handle FROM platform_shapes WHERE viewer_key = '' "
                + "AND last_read_at > ? AND (owner = ? OR lease_until < ?) ORDER BY lease_until, handle LIMIT ?)",
                ownerValue,
                until,
                SqlValues.Number((now - ActiveFor).UnixMs()),
                ownerValue,
                SqlValues.Number(now.UnixMs()),
                SqlValues.Number(limit)),
            SqlValues.Statement($"SELECT {RecordColumns} FROM platform_shapes WHERE owner = ? AND lease_until = ?", ownerValue, until),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Strong, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => new ClaimedShapes([.. results[1].RowsOf().Select(Record)]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<PageShapesOutcome> PageAsync(string after, int limit, CancellationToken cancellationToken)
    {
        var outcome = await store.ReadAsync(
            SqlValues.Statement($"SELECT {RecordColumns} FROM platform_shapes WHERE handle > ? ORDER BY handle LIMIT ?", SqlValues.Text(after), SqlValues.Number(limit)),
            cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => new ShapePage([.. results[0].RowsOf().Select(Record)]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public ValueTask<RetireShapeOutcome> RetireShapeAsync(string handle, CancellationToken cancellationToken) =>
        RetireAsync("handle = ?", SqlValues.Text(handle), [], cancellationToken);

    public ValueTask<RetireShapeOutcome> RetireDatabaseAsync(Ulid database, CancellationToken cancellationToken)
    {
        var id = SqlValues.Text(database.ToString());
        var now = timeProvider.GetUtcNow();
        return RetireAsync(
            "database_id = ?",
            id,
            [
                SqlValues.Statement(
                    "INSERT INTO platform_shape_database_retirements (database_id, requested_at) VALUES (?, ?) "
                    + "ON CONFLICT (database_id) DO UPDATE SET requested_at = excluded.requested_at",
                    id,
                    SqlValues.Number(now.UnixMs())),
                SqlValues.Statement(
                    "DELETE FROM platform_shape_database_retirements WHERE requested_at < ?", SqlValues.Number((now - RetiredDatabaseKept).UnixMs())),
            ],
            cancellationToken);
    }

    public async ValueTask<DueShapeDeletionsOutcome> DueDeletionsAsync(string after, int limit, CancellationToken cancellationToken)
    {
        var outcome = await store.ReadAsync(
            SqlValues.Statement(
                "SELECT d.id, d.handle, d.name, CASE WHEN d.name = ? THEN EXISTS (SELECT 1 FROM platform_shapes s WHERE s.handle = d.handle) "
                + "ELSE EXISTS (SELECT 1 FROM platform_shape_chunks c WHERE c.handle = d.handle AND c.object = d.name) END "
                + "FROM platform_shape_object_deletions d WHERE d.due_at <= ? AND d.id > ? ORDER BY d.id LIMIT ?",
                SqlValues.Text(HeadPointer),
                SqlValues.Number(timeProvider.GetUtcNow().UnixMs()),
                SqlValues.Text(after),
                SqlValues.Number(limit)),
            cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => new ShapeObjectDeletions(
                [.. results[0].RowsOf().Select(static row => new ShapeObjectDeletion(row.Text(0), row.Text(1), row.Text(2), row.Number(3) != 0))]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public ValueTask<UpdateShapeDeletionsOutcome> ClearDeletionsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return UpdateDeletionsAsync([.. ids.Select(static id => SqlValues.Statement("DELETE FROM platform_shape_object_deletions WHERE id = ?", SqlValues.Text(id)))], cancellationToken);
    }

    public ValueTask<UpdateShapeDeletionsOutcome> GuardObjectAsync(string handle, string name, string sealer, CancellationToken cancellationToken) =>
        UpdateDeletionsAsync(
            [SqlValues.Statement(
                "INSERT OR IGNORE INTO platform_shape_object_deletions (id, handle, name, due_at) VALUES (?, ?, ?, ?)",
                SqlValues.Text(Guard(handle, name, sealer)),
                SqlValues.Text(handle),
                SqlValues.Text(name),
                SqlValues.Number((timeProvider.GetUtcNow() + ObjectSettle).UnixMs()))],
            cancellationToken);

    private async ValueTask<UpdateShapeDeletionsOutcome> UpdateDeletionsAsync(IReadOnlyList<SqlHttpStatement> statements, CancellationToken cancellationToken)
    {
        if (statements.Count == 0)
        {
            return new ShapeDeletionsUpdated();
        }

        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse => new ShapeDeletionsUpdated(),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<RetireShapeOutcome> RetireAsync(
        string condition,
        JsonElement key,
        IReadOnlyList<SqlHttpStatement> first,
        CancellationToken cancellationToken)
    {
        var shapes = $"SELECT handle FROM platform_shapes WHERE {condition}";
        var now = SqlValues.Number(timeProvider.GetUtcNow().UnixMs());
        IReadOnlyList<SqlHttpStatement> statements =
        [
            .. first,
            SqlValues.Statement(shapes, key),
            SqlValues.Statement(
                "INSERT OR IGNORE INTO platform_shape_object_deletions (id, handle, name, due_at) "
                + $"SELECT handle || '/' || object, handle, object, ? FROM platform_shape_chunks WHERE handle IN ({shapes})",
                now,
                key),
            SqlValues.Statement(
                "INSERT OR IGNORE INTO platform_shape_object_deletions (id, handle, name, due_at) "
                + $"SELECT handle || '/' || ?, handle, ?, ? FROM platform_shapes WHERE {condition}",
                SqlValues.Text(HeadPointer),
                SqlValues.Text(HeadPointer),
                now,
                key),
            SqlValues.Statement($"DELETE FROM platform_shape_chunks WHERE handle IN ({shapes})", key),
            SqlValues.Statement($"DELETE FROM platform_shapes WHERE {condition}", key),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Strong, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => new ShapeRetirement([.. results[first.Count].RowsOf().Select(static row => row.Text(0))]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private static string Guard(string handle, string name, string sealer) => handle + "/" + name + "/" + sealer;

    private static ShapeChunks Chunks(SqlHttpStatementResult rows) => new([.. rows.RowsOf().Select(Chunk)]);

    private static ShapeChunkInfo Chunk(SqlRow row) =>
        new(row.Number(0), row.Text(1), row.Text(2), row.Number(3), row.Number(4), row.Text(5));

    private static ShapeRecord Record(SqlRow row) =>
        new(
            row.Text(0),
            row.Text(1),
            Ulid.Parse(row.Text(2)),
            Ulid.Parse(row.Text(3)),
            Ulid.Parse(row.Text(4)),
            row.Text(5),
            row.Text(6),
            row.Text(7),
            row.OptionalText(8),
            row.OptionalText(9),
            row.Text(10),
            row.Number(11),
            row.Text(12));
}
