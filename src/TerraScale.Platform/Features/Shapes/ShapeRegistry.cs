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
    public static readonly TimeSpan ActiveFor = TimeSpan.FromHours(1);
    private const string PointerObject = "head.json";
    private const string LineageHandle = "(SELECT handle FROM platform_shape_lineages WHERE shape_id = ?)";
    private const int DatabasePage = 1000;
    private const int ChunkSlice = 256;
    private const int CreateAttempts = 2;
    private const int RetirePasses = 32;
    private static readonly TimeSpan DeletionGrace = TimeSpan.FromDays(1);

    private const string RecordColumns =
        "handle, shape_id, organization_id, project_id, database_id, project_route, database_route, table_name, where_clause, columns, viewer_key, head, head_digest";

    private const string ChunkColumns = "chunk_offset, object, sha256, size, epoch, digest";

    public async ValueTask<RegisterShapeOutcome> EnsureAsync(ShapeRecord shape, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var now = timeProvider.GetUtcNow().UnixMs();
        var outcome = await store.ExecuteAsync([Touch(shape, now), Current(shape.ShapeId)], SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[1].FirstRow() is { } row => Record(row),
            SqlExecuteResponse => await CreateAsync(shape, now, CreateAttempts, cancellationToken),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<RegisterShapeOutcome> CreateAsync(ShapeRecord shape, long now, int attempts, CancellationToken cancellationToken)
    {
        var shapeId = SqlValues.Text(shape.ShapeId);
        var handle = SqlValues.Text(shape.Handle);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(
                "DELETE FROM platform_shape_lineages WHERE shape_id = ? AND NOT EXISTS (SELECT 1 FROM platform_shapes "
                + "WHERE platform_shapes.handle = platform_shape_lineages.handle AND platform_shapes.shape_id = platform_shape_lineages.shape_id)",
                shapeId),
            SqlValues.Statement("INSERT INTO platform_shape_lineages (shape_id, handle) VALUES (?, ?) ON CONFLICT (shape_id) DO NOTHING", shapeId, handle),
            SqlValues.Statement(
                "INSERT INTO platform_shapes (handle, shape_id, organization_id, project_id, database_id, project_route, database_route, table_name, "
                + "where_clause, columns, viewer_key, head, head_digest, sealer, owner, lease_until, last_read_at, created_at) "
                + $"SELECT ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, '', '', '', 0, ?, ? WHERE {LineageHandle} = ? ON CONFLICT (handle) DO NOTHING",
                handle,
                shapeId,
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
                shapeId,
                handle),
            Touch(shape, now),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Strong, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[2].RowsAffected == 1 => shape,
            SqlExecuteResponse created => await CurrentAsync(shape.ShapeId, created.Bookmark, cancellationToken),
            PlatformConflict when attempts > 1 => await CreateAsync(shape, now, attempts - 1, cancellationToken),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<RegisterShapeOutcome> CurrentAsync(string shapeId, string bookmark, CancellationToken cancellationToken)
    {
        var outcome = await store.ExecuteAsync([Current(shapeId)], SqlContext.After(bookmark), cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].FirstRow() is { } row => Record(row),
            SqlExecuteResponse => new PlatformUnavailable("the shape registration was not readable", "platform_state"),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private static SqlHttpStatement Touch(ShapeRecord shape, long now) =>
        SqlValues.Statement(
            $"UPDATE platform_shapes SET last_read_at = max(last_read_at, ?), project_route = ?, database_route = ? WHERE handle = {LineageHandle} AND shape_id = ?",
            SqlValues.Number(now),
            SqlValues.Text(shape.ProjectRoute),
            SqlValues.Text(shape.DatabaseRoute),
            SqlValues.Text(shape.ShapeId),
            SqlValues.Text(shape.ShapeId));

    private static SqlHttpStatement Current(string shapeId) =>
        SqlValues.Statement($"SELECT {RecordColumns} FROM platform_shapes WHERE handle = {LineageHandle} AND shape_id = ?", SqlValues.Text(shapeId), SqlValues.Text(shapeId));

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

        const string trimmed =
            "handle = ? AND chunk_offset IN (SELECT chunk_offset FROM platform_shape_chunks WHERE handle = ? AND chunk_offset <= ? ORDER BY chunk_offset LIMIT ?)";
        var key = SqlValues.Text(handle);
        var limit = SqlValues.Number(floor);
        var slice = SqlValues.Number(ChunkSlice);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement($"SELECT {ChunkColumns} FROM platform_shape_chunks WHERE {trimmed}", key, key, limit, slice),
            SqlValues.Statement(
                "INSERT OR IGNORE INTO platform_shape_object_deletions (id, handle, name, due_at) "
                + $"SELECT handle || '/' || object, handle, object, ? FROM platform_shape_chunks WHERE {trimmed}",
                SqlValues.Number((timeProvider.GetUtcNow() + DeletionGrace).UnixMs()),
                key,
                key,
                limit,
                slice),
            SqlValues.Statement($"DELETE FROM platform_shape_chunks WHERE {trimmed}", key, key, limit, slice),
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

    public async ValueTask<RetireShapeOutcome> RetireShapeAsync(string handle, CancellationToken cancellationToken)
    {
        var state = new RetireProgress(null, false, "");
        for (var pass = 0; pass < RetirePasses; pass++)
        {
            var step = await RetirePassAsync(handle, state, cancellationToken);
            (state, var finished) = step switch
            {
                RetireProgress next => (next, (RetireShapeOutcome?)null),
                ShapeRetirement retired => (state, retired),
                PlatformUnavailable unavailable => (state, unavailable),
            };
            if (finished is { } outcome)
            {
                return outcome;
            }
        }

        return new PlatformUnavailable($"retiring shape {handle} needs more than {RetirePasses} passes", "platform_state");
    }

    private sealed record ShapeFence(long Head, string Sealer, long CreatedAt);

    private sealed record RetireProgress(ShapeFence? Fence, bool Drained, string Bookmark);

    private readonly union RetireStep(RetireProgress, ShapeRetirement, PlatformUnavailable);

    private ValueTask<RetireStep> RetirePassAsync(string handle, RetireProgress state, CancellationToken cancellationToken) =>
        state switch
        {
            { Fence: null } => ReadFenceAsync(handle, state, cancellationToken),
            { Drained: false } => DrainChunksAsync(handle, state, cancellationToken),
            { Fence: { } fence } => FinishRetirementAsync(handle, fence, state, cancellationToken),
        };

    private async ValueTask<RetireStep> ReadFenceAsync(string handle, RetireProgress state, CancellationToken cancellationToken)
    {
        var context = state.Bookmark.Length == 0 ? SqlContext.Fast : SqlContext.After(state.Bookmark);
        var outcome = await store.ExecuteAsync([SqlValues.Statement("SELECT head, sealer, created_at FROM platform_shapes WHERE handle = ?", SqlValues.Text(handle))], context, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } read when results[0].FirstRow() is { } row => new RetireProgress(new ShapeFence(row.Number(0), row.Text(1), row.Number(2)), false, read.Bookmark),
            SqlExecuteResponse => new ShapeRetirement([]),
            PlatformConflict => state,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<RetireStep> DrainChunksAsync(string handle, RetireProgress state, CancellationToken cancellationToken)
    {
        const string sliced =
            "handle = ? AND chunk_offset IN (SELECT chunk_offset FROM platform_shape_chunks WHERE handle = ? ORDER BY chunk_offset LIMIT ?)";
        var key = SqlValues.Text(handle);
        var slice = SqlValues.Number(ChunkSlice);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(
                "INSERT OR IGNORE INTO platform_shape_object_deletions (id, handle, name, due_at) "
                + $"SELECT handle || '/' || object, handle, object, ? FROM platform_shape_chunks WHERE {sliced}",
                SqlValues.Number((timeProvider.GetUtcNow() + DeletionGrace).UnixMs()),
                key,
                key,
                slice),
            SqlValues.Statement($"DELETE FROM platform_shape_chunks WHERE {sliced}", key, key, slice),
        ];
        var outcome = await store.ExecuteAsync(statements, new SqlContext(state.Bookmark, SqlConsistencyHint.Strong), cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } drained => state with { Drained = results[1].RowsAffected < ChunkSlice, Bookmark = drained.Bookmark },
            PlatformConflict => state,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<RetireStep> FinishRetirementAsync(string handle, ShapeFence fence, RetireProgress state, CancellationToken cancellationToken)
    {
        const string fenced = "handle = ? AND head = ? AND sealer = ? AND created_at = ?";
        var key = SqlValues.Text(handle);
        var head = SqlValues.Number(fence.Head);
        var sealer = SqlValues.Text(fence.Sealer);
        var created = SqlValues.Number(fence.CreatedAt);
        var pointer = SqlValues.Text(PointerObject);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(
                "INSERT OR IGNORE INTO platform_shape_object_deletions (id, handle, name, due_at) "
                + $"SELECT handle || '/' || ?, handle, ?, ? FROM platform_shapes WHERE {fenced}",
                pointer,
                pointer,
                SqlValues.Number((timeProvider.GetUtcNow() + DeletionGrace).UnixMs()),
                key,
                head,
                sealer,
                created),
            SqlValues.Statement(
                $"DELETE FROM platform_shape_lineages WHERE shape_id = (SELECT shape_id FROM platform_shapes WHERE {fenced}) AND handle = ?",
                key,
                head,
                sealer,
                created,
                key),
            SqlValues.Statement($"DELETE FROM platform_shapes WHERE {fenced}", key, head, sealer, created),
        ];
        var outcome = await store.ExecuteAsync(statements, new SqlContext(state.Bookmark, SqlConsistencyHint.Strong), cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[2].RowsAffected == 1 => new ShapeRetirement([handle]),
            SqlExecuteResponse finished => new RetireProgress(null, false, finished.Bookmark),
            PlatformConflict => state,
            PlatformUnavailable unavailable => unavailable,
        };
    }

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
                SqlValues.Number((timeProvider.GetUtcNow() + DeletionGrace).UnixMs()))],
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
