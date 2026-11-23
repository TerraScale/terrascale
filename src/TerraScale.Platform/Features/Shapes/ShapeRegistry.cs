namespace TerraScale.Platform.Features.Shapes;

public sealed class ShapeRegistry(PlatformStore store, TimeProvider timeProvider)
{
    public const long RetainedChunks = 128;
    public static readonly TimeSpan ActiveFor = TimeSpan.FromHours(1);

    private const string RecordColumns =
        "handle, shape_id, organization_id, project_id, database_id, project_route, database_route, table_name, where_clause, columns, viewer_key, head, head_digest";

    private const string ChunkColumns = "chunk_offset, object, sha256, size, epoch, digest";

    public async ValueTask<Result<ShapeRecord, Unavailable>> EnsureAsync(ShapeRecord shape, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UnixMs();
        var handle = SqlValues.Text(shape.Handle);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(
                "INSERT INTO platform_shapes (handle, shape_id, organization_id, project_id, database_id, project_route, database_route, table_name, "
                + "where_clause, columns, viewer_key, head, head_digest, sealer, owner, lease_until, last_read_at, created_at) "
                + "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, '', '', '', 0, ?, ?) "
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
                SqlValues.Number(now)),
            SqlValues.Statement($"SELECT {RecordColumns} FROM platform_shapes WHERE handle = ?", handle),
        ];
        return await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results => results[1].FirstRow() is { } row
                ? Record(row)
                : new Unavailable("the shape registration was not readable", "platform_state"),
            Conflict conflict => new Unavailable(conflict.Message, "platform_state"),
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<ShapeRecord, NotFound, Unavailable>> ReadAsync(string handle, CancellationToken cancellationToken) =>
        await store.ReadAsync(SqlValues.Statement($"SELECT {RecordColumns} FROM platform_shapes WHERE handle = ?", SqlValues.Text(handle)), cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results => results[0].FirstRow() is { } row
                ? Record(row)
                : new NotFound("the shape is not registered", "shape_not_found"),
            Conflict conflict => new Unavailable(conflict.Message, "platform_state"),
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<IReadOnlyList<ShapeChunkInfo>, Unavailable>> ChunksAfterAsync(
        string handle,
        long after,
        int limit,
        CancellationToken cancellationToken) =>
        await store.ReadAsync(
            SqlValues.Statement(
                $"SELECT {ChunkColumns} FROM platform_shape_chunks WHERE handle = ? AND chunk_offset > ? ORDER BY chunk_offset LIMIT ?",
                SqlValues.Text(handle),
                SqlValues.Number(after),
                SqlValues.Number(limit)),
            cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results => Chunks(results[0]),
            Conflict conflict => new Unavailable(conflict.Message, "platform_state"),
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<ShapeChunkInfo, NotFound, Unavailable>> ChunkAsync(string handle, long offset, CancellationToken cancellationToken) =>
        await store.ReadAsync(
            SqlValues.Statement($"SELECT {ChunkColumns} FROM platform_shape_chunks WHERE handle = ? AND chunk_offset = ?", SqlValues.Text(handle), SqlValues.Number(offset)),
            cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results => results[0].FirstRow() is { } row
                ? Chunk(row)
                : new NotFound("the shape chunk is no longer retained", "shape_chunk_missing"),
            Conflict conflict => new Unavailable(conflict.Message, "platform_state"),
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<bool, Unavailable>> CommitAsync(ShapeRecord shape, ShapeChunkInfo chunk, long rows, CancellationToken cancellationToken)
    {
        var sealer = Ulid.NewUlid().ToString();
        var handle = SqlValues.Text(shape.Handle);
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
                SqlValues.Number(timeProvider.GetUtcNow().UnixMs()),
                handle,
                SqlValues.Text(sealer)),
        ];
        return await store.ExecuteAsync(statements, SqlContext.Strong, cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results => results[0].RowsAffected == 1 && results[1].RowsAffected == 1,
            Conflict => false,
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<IReadOnlyList<ShapeChunkInfo>, Unavailable>> TrimAsync(string handle, long head, CancellationToken cancellationToken)
    {
        var floor = head - RetainedChunks;
        if (floor <= 0)
        {
            return Items(Array.Empty<ShapeChunkInfo>());
        }

        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement($"SELECT {ChunkColumns} FROM platform_shape_chunks WHERE handle = ? AND chunk_offset <= ?", SqlValues.Text(handle), SqlValues.Number(floor)),
            SqlValues.Statement("DELETE FROM platform_shape_chunks WHERE handle = ? AND chunk_offset <= ?", SqlValues.Text(handle), SqlValues.Number(floor)),
        ];
        return await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results => Chunks(results[0]),
            Conflict conflict => new Unavailable(conflict.Message, "platform_state"),
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<IReadOnlyList<Ulid>, Unavailable>> ActiveDatabasesAsync(CancellationToken cancellationToken) =>
        await store.ReadAsync(
            SqlValues.Statement(
                "SELECT DISTINCT database_id FROM platform_shapes WHERE last_read_at > ?",
                SqlValues.Number((timeProvider.GetUtcNow() - ActiveFor).UnixMs())),
            cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results => Items(results[0].RowsOf().Select(static row => Ulid.Parse(row.Text(0))).ToArray()),
            Conflict conflict => new Unavailable(conflict.Message, "platform_state"),
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<IReadOnlyList<ShapeRecord>, Unavailable>> ClaimAsync(string owner, TimeSpan lease, int limit, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var ownerValue = SqlValues.Text(owner);
        var until = SqlValues.Number((now + lease).UnixMs());
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(
                "UPDATE platform_shapes SET owner = ?, lease_until = ? WHERE handle IN (SELECT handle FROM platform_shapes WHERE viewer_key = '' "
                + "AND last_read_at > ? AND (owner = ? OR lease_until < ?) LIMIT ?)",
                ownerValue,
                until,
                SqlValues.Number((now - ActiveFor).UnixMs()),
                ownerValue,
                SqlValues.Number(now.UnixMs()),
                SqlValues.Number(limit)),
            SqlValues.Statement($"SELECT {RecordColumns} FROM platform_shapes WHERE owner = ? AND lease_until = ?", ownerValue, until),
        ];
        return await store.ExecuteAsync(statements, SqlContext.Strong, cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results => Items(results[1].RowsOf().Select(Record).ToArray()),
            Conflict conflict => new Unavailable(conflict.Message, "platform_state"),
            Unavailable unavailable => unavailable,
        };
    }

    private static Result<IReadOnlyList<T>, Unavailable> Items<T>(IReadOnlyList<T> items) => new(items);

    private static Result<IReadOnlyList<ShapeChunkInfo>, Unavailable> Chunks(SqlHttpStatementResult rows) =>
        Items(rows.RowsOf().Select(Chunk).ToArray());

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
