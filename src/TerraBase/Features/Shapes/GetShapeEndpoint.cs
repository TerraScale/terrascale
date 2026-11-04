using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Features.Qos;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;
using static TerraBase.Features.Shapes.ShapeRoute;

namespace TerraBase.Features.Shapes;

internal sealed class GetShapeEndpoint(
    ITenantDirectory tenants, IShapeLineages lineages, IShapeSnapshots snapshots, IShapeChunks chunks, IShapeLiveTail live,
    IShapeMetrics metrics, IShapeFlights<ShapeRead> flights, IShapeMirror mirror, ShapeConnections connections) : EndpointWithoutRequest<IResult>
{
    private static readonly Validation BadOffset = new("offset must be -1, snap.<S>.<P> or a decimal commit boundary");

    public override void Configure()
    {
        Get(Shape);
        Name("getShape");
        Summary("Syncs a table shape");
        Description("""
            Requires sql:read. A shape is the rows of one ordinary rowid table that match where (every row when omitted), with the listed columns (every stored column when omitted), read from committed changes. Snapshots and sealed chunks come from the database home; live tails use the current region's replica when it holds the database. Send the same where and columns on every request; each pair has its own handle. offset=-1 answers 307 to the first page of the newest snapshot; follow next_offset from there: snap.<S>.<P> pages, then decimal commit boundaries, then live=true long-polls with the TB-Shape-Cursor bucket. Each operation is insert, update or delete with the row id as key; inserts and updates carry the shape's columns. A row that starts matching where arrives as an insert with move "in"; a row that stops matching arrives as a delete with move "out". Apply operations in order. handle is required on every request but the bootstrap. A changed table definition, a stale handle, an offset below retention, an offset above the home head, or an unretained snapshot page answers 409 must_refetch with the current handle in TB-Shape-Handle and the body; restart at offset=-1. A live offset ahead of the replica waits for catch-up and returns 503 unavailable if it still has not arrived. 429 shape_busy and 503 shape_too_large or unavailable: keep state and retry with backoff.
            """);
        Tags("Live");
        Produces<ShapeResponse>();
        Produces(StatusCodes.Status304NotModified);
        Produces(StatusCodes.Status307TemporaryRedirect);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ShapeErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(
            Query("offset", "-1, snap.<S>.<P>, or a decimal commit boundary.", required: true),
            Query("handle", "The handle of the previous response; required unless offset is -1."),
            Query("live", "true to long-poll at a decimal offset."),
            Query("cursor", "With live=true: the TB-Shape-Cursor bucket."),
            Query("where", "Optional SQL condition on this table's columns: literals, operators, CASE, CAST, COLLATE and deterministic functions; no subqueries or parameters."),
            Query("columns", "Optional comma-separated column names; rows carry only these columns."));
        RequireAuthorization(ApiKeyScopes.SqlRead);
        Metadata(new ListenerMetadata(TerraBaseListener.Client), OriginNoStore.Instance, new DatabaseRegions.ReplicaReads());
    }

    private static OpenApiParameter Query(string name, string description, bool required = false) => new()
    {
        Name = name,
        In = ParameterLocation.Query,
        Description = description,
        Required = required,
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var checkedRequest = CheckRequest();
        if (checkedRequest is not ShapeRequest request)
        {
            return ErrorResponse.From((Validation)checkedRequest);
        }

        var resolved = await ResolveAsync(HttpContext, tenants, lineages, cancellationToken);
        if (resolved is not ResolvedShape shape)
        {
            return Error(resolved);
        }

        var (scope, lineage) = (shape.Scope, shape.Lineage);
        if (request.Handle is { } handle)
        {
            if (request.Offset is Bootstrap)
            {
                return Refetch(HttpContext, lineage, "bootstrap cannot carry a handle");
            }

            if (!string.Equals(handle, lineage.Handle, StringComparison.Ordinal))
            {
                return Refetch(HttpContext, lineage, "shape lineage changed");
            }
        }

        if (request.Live)
        {
            if (connections.TryEnter(scope.Database) is not { } connection)
            {
                return ErrorResponse.From(new QuotaExceeded("shape connections are at capacity; retry later", "shape_busy"));
            }

            HttpContext.Response.RegisterForDispose(connection);
            HttpContext.Features.Get<QosRequestLease>()?.Lease.Dispose();
        }

        return request.Offset switch
        {
            Bootstrap => await snapshots.NewestAsync(scope, lineage, cancellationToken) switch
            {
                long snapshot => Redirect(scope, lineage, ShapeSnapshots.PageOffset(snapshot, 0)),
                MustRefetch refetch => Refetch(HttpContext, refetch.Lineage ?? lineage, refetch.Message),
                QuotaExceeded error => ErrorResponse.From(error),
                NotFound missing => ErrorResponse.From(missing),
                Unavailable error => ErrorResponse.From(error),
            },
            SnapshotOffset page => Answer(scope, request, lineage, await snapshots.PageAsync(scope, lineage, page.Snapshot, page.Page, cancellationToken)),
            DecimalOffset at when request.Live => Answer(scope, request, lineage, await live.WaitAsync(scope, lineage, at.Value, cancellationToken)),
            DecimalOffset at => Answer(scope, request, lineage, await chunks.ReadAsync(scope, lineage, at.Value, cancellationToken)),
            _ => throw new System.Diagnostics.UnreachableException(),
        };
    }

    private IResult Answer(ShapeScope scope, ShapeRequest request, ShapeLineage lineage, ShapeRead read)
    {
        if (read is not ShapeBody body)
        {
            return Error(read, HttpContext, lineage);
        }

        if (body.Lease is { } lease)
        {
            HttpContext.Response.RegisterForDispose(lease);
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(body.Json.Span).AsSpan(0, 8));
        var tag = $"{lineage.ShapeId}:{lineage.Handle}:{request.OffsetText}:{body.NextOffset}:{digest}";
        if (CheckNotModified(tag) is not bool notModified)
        {
            body.Lease?.Dispose();
            return ErrorResponse.From(new Validation("If-None-Match must be * or a list of entity tags"));
        }

        var headers = HttpContext.Response.Headers;
        if (!request.Live && !body.UpToDate && !notModified)
        {
            mirror.Warm(scope.Database, lineage.ShapeId, lineage.Handle, request.OffsetText, body.NextOffset);
        }

        headers.Vary = "Authorization";
        headers.ETag = $"\"{tag}\"";
        headers["TB-Shape-Hints"] = GetShapeHintEndpoint.Protocol;
        headers["TB-Shape-Handle"] = lineage.Handle;
        headers["TB-Shape-Next-Offset"] = body.NextOffset;
        headers["TB-Shape-Up-To-Date"] = body.UpToDate ? "true" : "false";
        if (request.Live)
        {
            headers["TB-Shape-Cursor"] = DatabaseRoute.Decimal(flights.NextCursor(request.Cursor));
        }

        metrics.RecordRead();
        HttpContext.Response.ContentType = DatabaseRoute.JsonMediaType;
        if (notModified)
        {
            body.Lease?.Dispose();
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        return new ShapeBytes(body.Json, body.Lease);
    }

    private bool? CheckNotModified(string tag)
    {
        if (HttpContext.Request.Headers.IfNoneMatch is not { Count: > 0 } values)
        {
            return false;
        }

        if (!EntityTagHeaderValue.TryParseStrictList(values, out var tags) || tags.Count == 0
            || (tags.Count > 1 && tags.Any(candidate => candidate.Equals(EntityTagHeaderValue.Any))))
        {
            return null;
        }

        return tags.Any(candidate => candidate.Equals(EntityTagHeaderValue.Any) || candidate.Tag.Value![1..^1] == tag);
    }

    private Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult Redirect(ShapeScope scope, ShapeLineage lineage, string offset)
    {
        var headers = HttpContext.Response.Headers;
        headers.Vary = "Authorization";
        headers["TB-Shape-Hints"] = GetShapeHintEndpoint.Protocol;
        headers["TB-Shape-Handle"] = lineage.Handle;
        HttpContext.Response.ContentType = DatabaseRoute.JsonMediaType;
        metrics.RecordRedirect();
        var location = $"/v1/projects/{Uri.EscapeDataString(DatabaseRoute.Project(HttpContext))}/databases/{DatabaseRoute.Database(HttpContext)}/tables/{scope.Table}/shape?handle={lineage.Handle}&offset={offset}{QueryString(scope.Query)}";
        return TypedResults.Redirect(location, permanent: false, preserveMethod: true);
    }

    private object CheckRequest()
    {
        if (DatabaseRoute.CheckParameters(HttpContext, "handle", "offset", "live", "cursor", "where", "columns") is Validation invalid)
        {
            return invalid;
        }

        var query = HttpContext.Request.Query;
        string? Parameter(string name)
        {
            return query.TryGetValue(name, out var value) ? value.ToString() : null;
        }
        var offsetText = Parameter("offset");
        if (offsetText is null || CheckOffset(offsetText) is not { } offset)
        {
            return BadOffset;
        }

        var isLive = Parameter("live") is not null;
        if (isLive && (Parameter("live") != "true" || offset is not DecimalOffset))
        {
            return new Validation("live requires a decimal offset and bucket");
        }

        var handle = Parameter("handle");
        if (handle is null && offset is not Bootstrap)
        {
            return new Validation("handle is required unless offset is -1");
        }

        if (handle is not null && !ShapeTables.IsHandle(handle))
        {
            return new Validation("handle must be h1- and 16 lowercase hex characters");
        }

        long? cursor = null;
        if (Parameter("cursor") is { } cursorText)
        {
            if (!isLive)
            {
                return new Validation("cursor requires live=true");
            }

            if (ShapeRoute.Decimal(cursorText) is not { } bucket || bucket == long.MaxValue)
            {
                return new Validation("cursor must be a decimal live bucket below 9223372036854775807");
            }

            cursor = bucket;
        }

        return new ShapeRequest(offset, offsetText, handle, isLive, cursor);
    }

    private sealed record ShapeRequest(Offset Offset, string OffsetText, string? Handle, bool Live, long? Cursor);

    private sealed class ShapeBytes(ReadOnlyMemory<byte> json, IDisposable? lease) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentLength = json.Length;
            using (lease)
            {
                await httpContext.Response.Body.WriteAsync(json, httpContext.RequestAborted);
            }
        }
    }
}
