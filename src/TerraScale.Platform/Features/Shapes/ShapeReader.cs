using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;

namespace TerraScale.Platform.Features.Shapes;

public readonly union ResolveShapeOutcome(
    ResolvedShape,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public readonly union ReadShapePageOutcome(ShapeAnswer, ShapeRefetch, PlatformUnavailable);

public readonly union ShapeManifestOutcome(ShapeMirrorManifest, ShapeRefetch, PlatformUnavailable);

public readonly union ShapeKeyOutcome(ShapeKeyResponse, ShapeRefetch, PlatformInvalidRequest);

public readonly union ReadShapeRowOutcome(
    RowResponse,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public sealed class ShapeReader(
    PlatformAccess access,
    TerraBaseClient terraBase,
    ShapeRegistry registry,
    ShapeSealer sealer,
    ShapeObjectStore objects,
    ShapeCrypto crypto,
    ShapeNotifier notifier,
    TimeProvider timeProvider)
{
    public const int PageRows = 1000;
    public const int MaxChanges = 32;
    public const int ManifestObjects = 8;
    private static readonly TimeSpan LiveWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LivePoll = TimeSpan.FromSeconds(2);
    private const int MaxLivePasses = 64;

    public async ValueTask<ResolveShapeOutcome> ResolveAsync(
        HttpContext context,
        ShapeRouteRequest route,
        ShapeQuery query,
        CancellationToken cancellationToken)
    {
        if (!objects.IsConfigured)
        {
            return new PlatformUnavailable("shape object storage is not configured", "shapes_unconfigured");
        }

        var outcome = await access.DatabaseAsync(context, route.Project, route.Database, Scopes.Read, cancellationToken);
        return outcome switch
        {
            DatabaseAccess database => await SnapshotAsync(context, route, query, database, cancellationToken),
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ReadShapePageOutcome> PageAsync(
        ResolvedShape resolved,
        SnapshotOffset offset,
        string offsetText,
        CancellationToken cancellationToken)
    {
        var shape = resolved.Shape;
        var outcome = await sealer.LoadAsync(shape.Handle, offset.Snapshot, cancellationToken);
        return outcome switch
        {
            ShapeChunk chunk when offset.Page > 0 && (long)offset.Page * PageRows >= chunk.Rows.Count => Refetch(shape, "the snapshot page is not retained"),
            ShapeChunk chunk => Page(shape, chunk, offset, offsetText),
            PlatformNotFound => Refetch(shape, "the snapshot is no longer retained"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ReadShapePageOutcome> ChangesAsync(
        ResolvedShape resolved,
        long after,
        string offsetText,
        long? cursor,
        CancellationToken cancellationToken)
    {
        var shape = resolved.Shape;
        var bucket = cursor is { } current ? NextCursor(current) : (long?)null;
        if (after > shape.Head)
        {
            return Refetch(shape, "the offset is above the shape head");
        }

        if (after == shape.Head)
        {
            return UpToDate(shape, after, offsetText, bucket);
        }

        var outcome = await registry.ChunksAfterAsync(shape.Handle, after, MaxChanges, cancellationToken);
        return outcome switch
        {
            ShapeChunks { Items: var chunks } when chunks.Count == 0 || chunks[0].Offset != after + 1 => Refetch(shape, "the offset is below retention"),
            ShapeChunks { Items: var chunks } => await CollectAsync(shape, chunks, 0, [], offsetText, bucket, cancellationToken),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ShapeReply> LiveAsync(
        HttpContext context,
        ShapeRouteRequest route,
        ResolvedShape resolved,
        long after,
        string offsetText,
        long cursor,
        CancellationToken cancellationToken) =>
        await WaitAsync(context, route, resolved, after, offsetText, cursor, timeProvider.GetUtcNow() + LiveWait, 0, cancellationToken);

    public async ValueTask<ShapeManifestOutcome> ManifestAsync(ShapeRecord shape, long after, CancellationToken cancellationToken)
    {
        if (after > shape.Head)
        {
            return Refetch(shape, "the offset is not published");
        }

        var outcome = await registry.ChunksAfterAsync(shape.Handle, after, ManifestObjects, cancellationToken);
        return outcome switch
        {
            ShapeChunks { Items: var chunks } when chunks.Count > 0 && chunks[0].Offset != after + 1 => Refetch(shape, "the offset is below retention"),
            ShapeChunks { Items: var chunks } => Manifest(shape, after, chunks),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public ShapeKeyOutcome Key(ShapeRecord shape, string epochText, string keyIdText)
    {
        if (ShapeOffset.ParseDecimal(epochText) is not { } epoch)
        {
            return new PlatformInvalidRequest("epoch must be a canonical unsigned decimal");
        }

        if (epoch > crypto.CurrentEpoch)
        {
            return new PlatformInvalidRequest("future epochs are refused");
        }

        if (epoch < crypto.CurrentEpoch - ShapeCrypto.RetainedEpochs || !Ulid.TryParse(keyIdText, out var keyId) || keyId != shape.Database)
        {
            return Refetch(shape, "the epoch and key are not retained for this handle");
        }

        var key = crypto.Key(shape.Handle, epoch, keyId);
        try
        {
            return new ShapeKeyResponse(shape.Handle, epoch.ToString(CultureInfo.InvariantCulture), keyId.ToString(), Base64Url.EncodeToString(key));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async ValueTask<ReadShapeRowOutcome> RowAsync(
        HttpContext context,
        GetRowRequest request,
        CancellationToken cancellationToken)
    {
        if (!ShapeQuery.IsTable(request.Table))
        {
            return ShapeQuery.BadTable;
        }

        if (ShapeOffset.ParseDecimal(request.Key) is not { } key)
        {
            return new PlatformInvalidRequest("key must be the row id as a decimal string");
        }

        var columns = request.Columns is { } list ? ShapeQuery.ColumnList(list) : null;
        if (request.Columns is not null && columns is null)
        {
            return new PlatformInvalidRequest("columns must be a comma-separated list of column names");
        }

        var projection = columns is { } names ? string.Join(", ", names.Select(ShapeQuery.Quote)) : "*";
        var includeRowid = columns?.Any(static name => string.Equals(name, "rowid", StringComparison.OrdinalIgnoreCase)) == true;
        var statement = SqlValues.Statement($"SELECT {projection} FROM {ShapeQuery.Quote(request.Table)} WHERE rowid = ?", SqlValues.Number(key));
        var outcome = await access.DatabaseAsync(context, request.Project, request.Database, Scopes.Read, cancellationToken);
        return outcome switch
        {
            DatabaseAccess database => await ReadRowAsync(database),
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<ReadShapeRowOutcome> ReadRowAsync(DatabaseAccess database)
        {
            var outcome = await terraBase.ExecuteAsCallerAsync(
                request.Project, request.Database, database.Access.Credential, SqlValues.Batch(statement), Bookmark(context), cancellationToken);
            return outcome switch
            {
                CallerSqlResponse sql => Row(sql.Response.Results[0], includeRowid) is { } row
                    ? row
                    : new PlatformNotFound("the row does not exist or is not readable", "row_not_found"),
                SqlFailure { Status: StatusCodes.Status400BadRequest } failure => new PlatformInvalidRequest(failure.Error.Message, failure.Error.Code),
                SqlFailure failure => new PlatformUnavailable(failure.Error.Message, failure.Error.Code),
                PlatformUnauthorized unauthorized => unauthorized,
                PlatformForbidden forbidden => forbidden,
                PlatformNotFound missing => missing,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }

    public string Location(ResolvedShape resolved, string offset) =>
        $"/v1/projects/{Uri.EscapeDataString(resolved.Shape.ProjectRoute)}/databases/{Uri.EscapeDataString(resolved.Shape.DatabaseRoute)}/tables/{resolved.Query.Table}/shape"
        + $"?handle={resolved.Shape.Handle}&offset={offset}{resolved.Query.QueryString()}";

    public static ShapeRefetch Refetch(ShapeRecord shape, string message) => new(shape.Handle, message);

    private async ValueTask<ShapeReply> WaitAsync(
        HttpContext context,
        ShapeRouteRequest route,
        ResolvedShape current,
        long after,
        string offsetText,
        long cursor,
        DateTimeOffset deadline,
        int pass,
        CancellationToken cancellationToken)
    {
        if (current.Shape.Head > after)
        {
            var changes = await ChangesAsync(current, after, offsetText, cursor, cancellationToken);
            return changes switch
            {
                ShapeAnswer answer => answer,
                ShapeRefetch refetch => refetch,
                PlatformUnavailable unavailable => unavailable,
            };
        }

        var remaining = deadline - timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero || pass >= MaxLivePasses)
        {
            return UpToDate(current.Shape, after, offsetText, NextCursor(cursor));
        }

        await notifier.WaitAsync(current.Shape.Database, remaining < LivePoll ? remaining : LivePoll, cancellationToken);
        var outcome = await ResolveAsync(context, route, current.Query, cancellationToken);
        return outcome switch
        {
            ResolvedShape next when next.Shape.Handle != current.Shape.Handle => Refetch(next.Shape, "shape lineage changed"),
            ResolvedShape next => await WaitAsync(context, route, next, after, offsetText, cursor, deadline, pass + 1, cancellationToken),
            PlatformInvalidRequest invalid => invalid,
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ResolveShapeOutcome> SnapshotAsync(
        HttpContext context,
        ShapeRouteRequest route,
        ShapeQuery query,
        DatabaseAccess database,
        CancellationToken cancellationToken)
    {
        var outcome = await terraBase.ExecuteAsCallerAsync(route.Project, route.Database, database.Access.Credential, SqlValues.Batch(query.Select()), Bookmark(context), cancellationToken);
        return outcome switch
        {
            CallerSqlResponse sql => ShapeQuery.Snapshot(sql.Response.Results[0], query.IncludesRowid) switch
            {
                ShapeSnapshot snapshot => await RegisterSnapshotAsync(snapshot, sql.RulesVersion),
                PlatformUnavailable unavailable => unavailable,
            },
            SqlFailure { Status: StatusCodes.Status400BadRequest } failure => new PlatformInvalidRequest(failure.Error.Message, failure.Error.Code),
            SqlFailure failure => new PlatformUnavailable(failure.Error.Message, failure.Error.Code),
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<ResolveShapeOutcome> RegisterSnapshotAsync(ShapeSnapshot snapshot, string? rulesVersion)
        {
            var outcome = await RegisterAsync(route, query, database, snapshot, rulesVersion, cancellationToken);
            return outcome switch
            {
                ResolvedShape resolved => resolved,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }

    private readonly union RegisterResolvedShapeOutcome(ResolvedShape, PlatformUnavailable);

    private async ValueTask<RegisterResolvedShapeOutcome> RegisterAsync(
        ShapeRouteRequest route,
        ShapeQuery query,
        DatabaseAccess database,
        ShapeSnapshot snapshot,
        string? rulesVersion,
        CancellationToken cancellationToken)
    {
        var viewer = ShapeViewers.Key(database.Access.Grant, rulesVersion);
        var (handle, shapeId) = query.Lineage(database.Access.Organization, database.Access.Project, database.Database, viewer, snapshot.Columns);
        var shape = new ShapeRecord(handle, shapeId, database.Access.Organization, database.Access.Project, database.Database, route.Project, route.Database,
            query.Table, query.Where, query.Columns, viewer, 0, "");
        var outcome = await registry.EnsureAsync(shape, cancellationToken);
        return outcome switch
        {
            ShapeRecord saved => await SyncAsync(saved),
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<RegisterResolvedShapeOutcome> SyncAsync(ShapeRecord saved)
        {
            var outcome = await sealer.SyncAsync(saved, snapshot, cancellationToken);
            return outcome switch
            {
                ShapeRecord synced => new ResolvedShape(synced, database, query),
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }

    private async ValueTask<ReadShapePageOutcome> CollectAsync(
        ShapeRecord shape,
        IReadOnlyList<ShapeChunkInfo> chunks,
        int index,
        IReadOnlyList<ShapeChange> changes,
        string offsetText,
        long? cursor,
        CancellationToken cancellationToken)
    {
        if (index == chunks.Count || chunks[index].Offset != chunks[0].Offset + index)
        {
            var next = changes[^1].Offset;
            return new ShapeAnswer(
                new ShapeResponse(shape.ShapeId, shape.Handle, offsetText, next, next, long.Parse(next, CultureInfo.InvariantCulture) == shape.Head, changes),
                cursor);
        }

        var outcome = await sealer.LoadAsync(shape.Handle, chunks[index].Offset, cancellationToken);
        return outcome switch
        {
            ShapeChunk chunk => await CollectAsync(
                shape,
                chunks,
                index + 1,
                [.. changes, new ShapeChange(chunk.Offset.ToString(CultureInfo.InvariantCulture), chunk.Ops)],
                offsetText,
                cursor,
                cancellationToken),
            PlatformNotFound => Refetch(shape, "a shape chunk is no longer retained"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private static ShapeAnswer Page(ShapeRecord shape, ShapeChunk chunk, SnapshotOffset offset, string offsetText)
    {
        var start = offset.Page * PageRows;
        var rows = chunk.Rows.Skip(start).Take(PageRows).Select(static row => new ShapeOperation("insert", row.Key, row.Row)).ToArray();
        var last = start + PageRows >= chunk.Rows.Count;
        var snapshot = offset.Snapshot.ToString(CultureInfo.InvariantCulture);
        var next = last ? snapshot : $"snap.{snapshot}.{offset.Page + 1}";
        IReadOnlyList<ShapeChange> changes = rows.Length == 0 ? [] : [new ShapeChange(offsetText, rows)];
        return new ShapeAnswer(new ShapeResponse(shape.ShapeId, shape.Handle, offsetText, next, snapshot, last && offset.Snapshot == shape.Head, changes), null);
    }

    private static ShapeAnswer UpToDate(ShapeRecord shape, long after, string offsetText, long? cursor)
    {
        var at = after.ToString(CultureInfo.InvariantCulture);
        return new ShapeAnswer(new ShapeResponse(shape.ShapeId, shape.Handle, offsetText, at, at, true, []), cursor);
    }

    private long NextCursor(long cursor) => Math.Max(cursor + 1, timeProvider.GetUtcNow().ToUnixTimeSeconds() / 20);

    private ShapeMirrorManifest Manifest(ShapeRecord shape, long after, IReadOnlyList<ShapeChunkInfo> chunks)
    {
        var expires = crypto.EpochEnd(crypto.CurrentEpoch);
        var previous = after;
        var objectsList = new List<ShapeMirrorObject>(chunks.Count);
        foreach (var chunk in chunks)
        {
            if (chunk.Offset != previous + 1)
            {
                break;
            }

            objectsList.Add(new ShapeMirrorObject(
                previous.ToString(CultureInfo.InvariantCulture),
                chunk.Offset.ToString(CultureInfo.InvariantCulture),
                "tbs1",
                chunk.Name,
                objects.Url(shape.Handle, chunk.Name),
                expires,
                chunk.Epoch.ToString(CultureInfo.InvariantCulture),
                shape.Database.ToString(),
                chunk.Sha256,
                chunk.Size));
            previous = chunk.Offset;
        }

        return new ShapeMirrorManifest(shape.Handle, objectsList);
    }

    private static RowResponse? Row(SqlHttpStatementResult result, bool includeRowid)
    {
        if (result.FirstRow() is not { } row)
        {
            return null;
        }

        var skip = !includeRowid && result.Columns.Count > 1 && string.Equals(result.Columns[0].Name, "rowid", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        return new RowResponse(ShapeQuery.RowObject([.. result.Columns.Skip(skip).Select(static column => column.Name)], row, skip));
    }

    private static string Bookmark(HttpContext context) =>
        context.Request.Headers[ConsistencyHeaders.Bookmark] is { Count: 1 } values && values[0] is { } bookmark && CausalBookmark.TryDecode(bookmark, out _)
            ? bookmark
            : "";
}
