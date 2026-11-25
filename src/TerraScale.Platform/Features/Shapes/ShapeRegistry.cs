namespace TerraScale.Platform.Features.Shapes;

public sealed record ShapeChunks(IReadOnlyList<ShapeChunkInfo> Items);

public sealed record ActiveShapeDatabases(IReadOnlyList<Ulid> Items);

public sealed record ClaimedShapes(IReadOnlyList<ShapeRecord> Items);

public sealed record ShapePage(IReadOnlyList<ShapeRecord> Items);

public sealed record ShapeObjectDeletion(string Id, string Handle, string Name);

public sealed record ShapeObjectDeletions(IReadOnlyList<ShapeObjectDeletion> Items);

public sealed record ShapeDeletionsUpdated;

public readonly union RegisterShapeOutcome(ShapeRecord, PlatformUnavailable);

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
    public const int RetireBatch = 128;
    public static readonly TimeSpan ActiveFor = TimeSpan.FromHours(1);
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(1);
    private const string PointerObject = "head.json";
    private const int DatabasePage = 1000;
    private static readonly TimeSpan ObjectSettle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RolloutGrace = TimeSpan.FromDays(1);

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
                + "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, '', '', '', 0, ?, ?) "
                + "ON CONFLICT (shape_id) DO UPDATE SET last_read_at = max(last_read_at, excluded.last_read_at), "
                + "project_route = excluded.project_route, database_route = excluded.database_route",
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
                SqlValues.Number(now)),
            SqlValues.Statement($"SELECT {RecordColumns} FROM platform_shapes WHERE shape_id = ?", SqlValues.Text(shape.ShapeId)),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => results[1].FirstRow() is { } row
                ? Record(row)
                : new PlatformUnavailable("the shape registration was not readable", "platform_state"),
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
        var guard = SqlValues.Text(Guard(shape.Handle, chunk.Name, sealer));
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(
                "UPDATE platform_shapes SET head = ?, head_digest = ?, sealer = ? WHERE handle = ? AND head = ? "
                + "AND EXISTS (SELECT 1 FROM platform_shape_object_deletions WHERE id = ? AND claimed = 0)",
                SqlValues.Number(chunk.Offset),
                SqlValues.Text(chunk.Digest),
                SqlValues.Text(sealer),
                handle,
                SqlValues.Number(shape.Head),
                guard),
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
                SqlValues.Number(timeProvider.GetUtcNow().UnixMs()),
                handle,
                SqlValues.Text(sealer)),
            SqlValues.Statement(
                "DELETE FROM platform_shape_object_deletions WHERE id = ? AND claimed = 0 AND (SELECT sealer FROM platform_shapes WHERE handle = ?) = ?",
                guard,
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

    public async ValueTask<TrimShapeChunksOutcome> TrimAsync(string handle, long head, CancellationToken cancellationToken)
    {
        var floor = head - RetainedChunks;
        if (floor <= 0)
        {
            return new ShapeChunks([]);
        }

        var key = SqlValues.Text(handle);
        var limit = SqlValues.Number(floor);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement($"SELECT {ChunkColumns} FROM platform_shape_chunks WHERE handle = ? AND chunk_offset <= ?", key, limit),
            SqlValues.Statement(
                "INSERT OR IGNORE INTO platform_shape_object_deletions (id, handle, name, due_at) "
                + "SELECT handle || '/' || object, handle, object, ? FROM platform_shape_chunks WHERE handle = ? AND chunk_offset <= ?",
                SqlValues.Number((timeProvider.GetUtcNow() + RolloutGrace).UnixMs()),
                key,
                limit),
            SqlValues.Statement("DELETE FROM platform_shape_chunks WHERE handle = ? AND chunk_offset <= ?", key, limit),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => Chunks(results[0]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public ValueTask<ActiveShapeDatabasesOutcome> ActiveDatabasesAsync(CancellationToken cancellationToken) =>
        ActiveDatabasesAfterAsync("", [], cancellationToken);

    private async ValueTask<ActiveShapeDatabasesOutcome> ActiveDatabasesAfterAsync(string after, IReadOnlyList<Ulid> found, CancellationToken cancellationToken)
    {
        var outcome = await store.ReadAsync(
            SqlValues.Statement(
                "SELECT DISTINCT database_id FROM platform_shapes WHERE last_read_at > ? AND database_id > ? ORDER BY database_id LIMIT ?",
                SqlValues.Number((timeProvider.GetUtcNow() - ActiveFor).UnixMs()),
                SqlValues.Text(after),
                SqlValues.Number(DatabasePage)),
            cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => await NextAsync(results[0]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<ActiveShapeDatabasesOutcome> NextAsync(SqlHttpStatementResult page)
        {
            string[] ids = [.. page.RowsOf().Select(static row => row.Text(0))];
            IReadOnlyList<Ulid> all = [.. found, .. ids.Select(Ulid.Parse)];
            return ids.Length < DatabasePage ? new ActiveShapeDatabases(all) : await ActiveDatabasesAfterAsync(ids[^1], all, cancellationToken);
        }
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
        RetireAsync("handle = ?", [SqlValues.Text(handle)], cancellationToken);

    public ValueTask<RetireShapeOutcome> RetireIdleAsync(CancellationToken cancellationToken) =>
        RetireAsync(
            "handle IN (SELECT handle FROM platform_shapes WHERE last_read_at < ? ORDER BY handle LIMIT ?)",
            [SqlValues.Number((timeProvider.GetUtcNow() - KeptFor).UnixMs()), SqlValues.Number(RetireBatch)],
            cancellationToken);

    public async ValueTask<DueShapeDeletionsOutcome> DueDeletionsAsync(string after, int limit, CancellationToken cancellationToken)
    {
        var outcome = await store.ReadAsync(
            SqlValues.Statement(
                "SELECT id, handle, name FROM platform_shape_object_deletions WHERE due_at <= ? AND id > ? ORDER BY id LIMIT ?",
                SqlValues.Number(timeProvider.GetUtcNow().UnixMs()),
                SqlValues.Text(after),
                SqlValues.Number(limit)),
            cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => new ShapeObjectDeletions(
                [.. results[0].RowsOf().Select(static row => new ShapeObjectDeletion(row.Text(0), row.Text(1), row.Text(2)))]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DueShapeDeletionsOutcome> ClaimDeletionsAsync(IReadOnlyList<ShapeObjectDeletion> due, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(due);
        if (due.Count == 0)
        {
            return new ShapeObjectDeletions([]);
        }

        const string referenced =
            "EXISTS (SELECT 1 FROM platform_shape_chunks c WHERE c.handle = platform_shape_object_deletions.handle AND c.object = platform_shape_object_deletions.name)";
        List<SqlHttpStatement> statements = [];
        foreach (var item in due)
        {
            var id = SqlValues.Text(item.Id);
            statements.Add(SqlValues.Statement($"DELETE FROM platform_shape_object_deletions WHERE id = ? AND claimed = 0 AND {referenced}", id));
            statements.Add(SqlValues.Statement($"UPDATE platform_shape_object_deletions SET claimed = 1 WHERE id = ? AND NOT {referenced}", id));
        }

        var outcome = await store.ExecuteAsync(statements, SqlContext.Strong, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => new ShapeObjectDeletions([.. due.Where((_, index) => results[(index * 2) + 1].RowsAffected == 1)]),
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

    private async ValueTask<RetireShapeOutcome> RetireAsync(string condition, IReadOnlyList<JsonElement> keys, CancellationToken cancellationToken)
    {
        var shapes = $"SELECT handle FROM platform_shapes WHERE {condition}";
        var now = SqlValues.Number((timeProvider.GetUtcNow() + RolloutGrace).UnixMs());
        var pointer = SqlValues.Text(PointerObject);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(shapes, [.. keys]),
            SqlValues.Statement(
                "INSERT OR IGNORE INTO platform_shape_object_deletions (id, handle, name, due_at) "
                + $"SELECT handle || '/' || object, handle, object, ? FROM platform_shape_chunks WHERE handle IN ({shapes})",
                [now, .. keys]),
            SqlValues.Statement(
                "INSERT OR IGNORE INTO platform_shape_object_deletions (id, handle, name, due_at) "
                + $"SELECT handle || '/' || ?, handle, ?, ? FROM platform_shapes WHERE {condition}",
                [pointer, pointer, now, .. keys]),
            SqlValues.Statement($"DELETE FROM platform_shape_chunks WHERE handle IN ({shapes})", [.. keys]),
            SqlValues.Statement($"DELETE FROM platform_shapes WHERE {condition}", [.. keys]),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Strong, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => new ShapeRetirement([.. results[0].RowsOf().Select(static row => row.Text(0))]),
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
