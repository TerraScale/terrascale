using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Documents;
using TerraBase.Features.Qos;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Shapes;

/// <summary><c>GET …/collections/{collection}/shape</c>: a collection's shape as snapshot pages, chunks and a live tail (shape reads v1).</summary>
internal sealed class GetShapeEndpoint(
    IDocumentService documents, ITenantDirectory tenants, IShapeLineages lineages, IShapeSnapshots snapshots, IShapeChunks chunks, IShapeLiveTail live,
    IShapeResume resumes, IShapeMetrics metrics, IShapeFlights<ShapeRead> flights, IShapeMirror mirror, IStorageEngine engine) : EndpointWithoutRequest<IResult>
{
    private const string ResumeHeader = "TB-Shape-Resume";

    // A continuation target fits the 8 KiB request line with 64 bytes reserved for the method and version (section 2).
    private const int MaxTarget = (8 * 1024) - 64;

    // The longest handle, and the longest offset: snap.<long.MaxValue>.<base64url of a 256-character id, at most 768 UTF-8 bytes>.
    private const int MaxHandle = 19;
    private static readonly int MaxOffset = "snap.".Length + 19 + 1 + Base64Url.GetEncodedLength(256 * 3);

    // Envelope: marker, 16 scope bytes, then a changes feed cursor of at most 256 origins.
    private const int ResumeScopeBytes = 16;
    private static readonly int MaxResume = Base64Url.GetEncodedLength(1 + ResumeScopeBytes + ChangeCursor.MaxBytes);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Validation BadOffset = new("offset must be -1, snap.<S>.<P> or a decimal commit boundary");
    private static readonly Validation BadResume = new("resume cursor is malformed");

    /// <inheritdoc />
    public override void Configure()
    {
        Get(DocumentRoute.Collections + "/{collection}/shape");
        Name("getShape");
        Summary("Syncs a collection shape");
        Description("""
            Requires documents:read. End-user tokens also require the current collection read rules; owner grants sync only that signed subject's documents. Policy changes invalidate the handle, including during live waits. End-user sync uses this origin endpoint; mirror manifests and epoch keys are refused. A shape is the collection plus an optional where filter (the filter grammar of POST …/query). offset=-1 answers 307 to the first page of the newest published snapshot on this machine's handle; follow next_offset from there: snap.<S>.<P> pages, then decimal commit boundaries, then live=true long-polls with the TB-Shape-Cursor bucket. handle is required on every request but the bootstrap. A stale handle, an offset below retention or above the sealed head, or an unretained snapshot page answers 409 must_refetch with the current handle in TB-Shape-Handle and the body; restart at offset=-1. offset=-1 without a handle and with an opaque cursor_resume in cursor or the TB-Shape-Resume header resumes on another machine, or answers 409 must_refetch. A continuation target exceeding 8 KiB minus the 64-byte request-line reservation answers 414 uri_too_long. Larger incoming request lines can be rejected by the listener before JSON error handling; use TB-Shape-Resume for large resume tokens. 429 shape_busy and 503 shape_too_large or unavailable: keep state and retry with backoff.
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
        Produces<ErrorBody>(StatusCodes.Status414UriTooLong);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(
            Query("offset", "-1, snap.<S>.<P>, or a decimal commit boundary.", required: true),
            Query("handle", "The handle of the previous response; required unless offset is -1."),
            Query("where", "JSON filter in the grammar of POST …/query."),
            Query("live", "true to long-poll at a decimal offset."),
            Query("cursor", "With live=true: the TB-Shape-Cursor bucket. With offset=-1 and no handle: an opaque resume cursor."),
            new OpenApiParameter
            {
                Name = ResumeHeader,
                In = ParameterLocation.Header,
                Description = "The resume cursor, when the request target would not fit it; read only with offset=-1 and no handle.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(new ListenerMetadata(TerraBaseListener.Client), OriginNoStore.Instance);
    }

    private static OpenApiParameter Query(string name, string description, bool required = false) => new()
    {
        Name = name,
        In = ParameterLocation.Query,
        Description = description,
        Required = required,
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DocumentRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => DocumentRoute.CheckCollectionName(HttpContext) switch
            {
                Validation error => ErrorResponse.From(error),
                string name => await documents.GetCollectionAsync(database, name, cancellationToken) switch
                {
                    NotFound error => ErrorResponse.From(error),
                    Conflict error => ErrorResponse.From(error),
                    Unavailable error => ErrorResponse.From(error),
                    CollectionInfo collection => CheckRequest(database, collection) switch
                    {
                        Validation error => ErrorResponse.From(error),
                        ShapeRequest request => Location(request, MaxHandle, new string('0', MaxOffset)).Length > MaxTarget
                            ? new UriTooLong()
                            : await ServeAsync(request, cancellationToken),
                    },
                },
            },
        };

    private async ValueTask<IResult> ServeAsync(ShapeRequest request, CancellationToken cancellationToken)
    {
        var bound = await ShapeAccess.BindAsync(engine, request.Scope, ProjectAccess.Current, cancellationToken);
        if (bound is not ShapeScope scope)
        {
            return bound switch
            {
                Forbidden denied => ErrorResponse.From(denied),
                NotFound missing => ErrorResponse.From(missing),
                Unavailable unavailable => ErrorResponse.From(unavailable),
                ShapeScope => throw new System.Diagnostics.UnreachableException(),
            };
        }

        request = request with { Scope = scope };
        var answer = await ServeAuthorizedAsync(request, cancellationToken);
        if (scope.Filter.Access is null)
        {
            return answer;
        }

        var checkedAccess = await ShapeAccess.BindAsync(engine, scope, ProjectAccess.Current, cancellationToken);
        if (checkedAccess is ShapeScope current && current.CanonicalFilter == scope.CanonicalFilter && current.Collection.Id == scope.Collection.Id)
        {
            return answer;
        }

        // No successful-response metadata survives a policy change during a wait or seal.
        foreach (var header in new[] { "ETag", "TB-Shape-Handle", "TB-Shape-Next-Offset", "TB-Shape-Up-To-Date", "TB-Shape-Cursor" })
        {
            HttpContext.Response.Headers.Remove(header);
        }
        return checkedAccess switch
        {
            Forbidden denied => ErrorResponse.From(denied),
            NotFound missing => ErrorResponse.From(missing),
            Unavailable unavailable => ErrorResponse.From(unavailable),
            ShapeScope changed => await PolicyChangedAsync(changed, cancellationToken),
        };
    }

    private async ValueTask<IResult> PolicyChangedAsync(ShapeScope scope, CancellationToken cancellationToken) =>
        await lineages.ResolveAsync(scope, cancellationToken) switch
        {
            ShapeLineage current => Refetch(current, "shape policy changed; restart at offset=-1"),
            Evicted evicted => Refetch(evicted.Lineage, "shape policy changed; restart at offset=-1"),
            Forbidden denied => ErrorResponse.From(denied),
            NotFound missing => ErrorResponse.From(missing),
            QuotaExceeded busy => ErrorResponse.From(busy),
            Unavailable unavailable => ErrorResponse.From(unavailable),
        };

    private async ValueTask<IResult> ServeAuthorizedAsync(ShapeRequest request, CancellationToken cancellationToken)
    {
        var resolved = await lineages.ResolveAsync(request.Scope, cancellationToken);
        if (resolved is not ShapeLineage lineage)
        {
            return resolved switch
            {
                Evicted evicted => Refetch(evicted.Lineage, "shape lineage changed or filter evicted"),
                QuotaExceeded error => ErrorResponse.From(error),
                Forbidden denied => ErrorResponse.From(denied),
                NotFound missing => ErrorResponse.From(missing),
                Unavailable error => ErrorResponse.From(error),
                ShapeLineage => throw new System.Diagnostics.UnreachableException(),
            };
        }

        if (request.Handle is { } handle)
        {
            if (request.Offset is Bootstrap)
            {
                return Refetch(lineage, "bootstrap cannot carry a handle");
            }

            if (!string.Equals(handle, lineage.Handle, StringComparison.Ordinal))
            {
                return Refetch(lineage, "shape lineage changed");
            }
        }

        if (request.Live && request.Offset is DecimalOffset)
        {
            // Long polls use the flight's bounded admission, freeing the organization's request slots for writes.
            HttpContext.Features.Get<QosRequestLease>()?.Lease.Dispose();
        }

        return request.Offset switch
        {
            Bootstrap when request.Resume is { } resume => await resumes.ResolveAsync(request.Scope, lineage, resume.Scope, resume.Cursor, cancellationToken) switch
            {
                long target => Redirect(request, lineage, DocumentRoute.Decimal(target)),
                MustRefetch refetch => Refetch(refetch.Lineage ?? lineage, refetch.Message),
                Unavailable error => ErrorResponse.From(error),
            },
            Bootstrap => await snapshots.NewestAsync(request.Scope, lineage, cancellationToken) switch
            {
                long snapshot => Redirect(request, lineage, $"snap.{DocumentRoute.Decimal(snapshot)}.0"),
                MustRefetch refetch => Refetch(refetch.Lineage ?? lineage, refetch.Message),
                QuotaExceeded error => ErrorResponse.From(error),
                Forbidden denied => ErrorResponse.From(denied),
                NotFound missing => ErrorResponse.From(missing),
                Unavailable error => ErrorResponse.From(error),
            },
            SnapshotOffset page => Answer(request, lineage, await snapshots.PageAsync(request.Scope, lineage, page.Snapshot, page.AfterId, cancellationToken)),
            DecimalOffset at when request.Live => Answer(request, lineage, await live.WaitAsync(request.Scope, lineage, at.Value, cancellationToken)),
            DecimalOffset at => Answer(request, lineage, await chunks.ReadAsync(request.Scope, lineage, at.Value, cancellationToken)),
            _ => throw new System.Diagnostics.UnreachableException(),
        };
    }

    private IResult Answer(ShapeRequest request, ShapeLineage lineage, ShapeRead read)
    {
        if (read is not ShapeBody body)
        {
            return read switch
            {
                MustRefetch refetch => Refetch(refetch.Lineage ?? lineage, refetch.Message),
                QuotaExceeded error => ErrorResponse.From(error),
                Forbidden denied => ErrorResponse.From(denied),
                NotFound missing => ErrorResponse.From(missing),
                Unavailable error => ErrorResponse.From(error),
                ShapeBody => throw new System.Diagnostics.UnreachableException(),
            };
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
        if (request.Scope.Filter.Access is null && !request.Live && !body.UpToDate && !notModified)
        {
            mirror.Warm(request.Scope.Database, lineage.ShapeId, lineage.Handle, request.OffsetText, body.NextOffset);
        }

        headers.Vary = "Authorization";
        headers.ETag = $"\"{tag}\"";
        headers["TB-Shape-Handle"] = lineage.Handle;
        headers["TB-Shape-Next-Offset"] = body.NextOffset;
        headers["TB-Shape-Up-To-Date"] = body.UpToDate ? "true" : "false";
        if (request.Live)
        {
            // Strictly increasing per client even when the clock steps back; the input is below long.MaxValue.
            headers["TB-Shape-Cursor"] = DocumentRoute.Decimal(flights.NextCursor(request.Cursor));
        }

        metrics.RecordRead();
        HttpContext.Response.ContentType = DocumentRoute.JsonMediaType;
        if (notModified)
        {
            body.Lease?.Dispose();
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        return new ShapeBytes(body.Json, body.Lease);
    }

    // If-None-Match only, with the strict list parser of document reads: weak tags and a bare * match; a quoted "*" is an
    // ordinary tag. Null when the list is malformed.
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

    private Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult Redirect(ShapeRequest request, ShapeLineage lineage, string offset)
    {
        var headers = HttpContext.Response.Headers;
        headers.Vary = "Authorization";
        headers["TB-Shape-Handle"] = lineage.Handle;
        HttpContext.Response.ContentType = DocumentRoute.JsonMediaType;
        metrics.RecordRedirect();
        return TypedResults.Redirect(Location(request, lineage.Handle.Length, offset, lineage.Handle), permanent: false, preserveMethod: true);
    }

    private Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ShapeErrorBody> Refetch(ShapeLineage lineage, string message)
    {
        HttpContext.Response.Headers["TB-Shape-Handle"] = lineage.Handle;
        return TypedResults.Json(
            new ShapeErrorBody(new ShapeErrorDetail("must_refetch", message, lineage.Handle)), ShapeJsonContext.Default.ShapeErrorBody,
            statusCode: StatusCodes.Status409Conflict);
    }

    // The continuation target: where (canonical), then handle, then offset, so clients converge on identical URLs.
    private string Location(ShapeRequest request, int handleLength, string offset, string? handle = null) =>
        $"/v1/projects/{Uri.EscapeDataString(DocumentRoute.Project(HttpContext))}/databases/{DocumentRoute.Database(HttpContext)}/collections/{request.Scope.Collection.Name}/shape?"
        + (request.Scope.Filter.RequestCanonical.Length == 0 ? "" : $"where={Uri.EscapeDataString(request.Scope.Filter.RequestCanonical)}&")
        + $"handle={handle ?? new string('0', handleLength)}&offset={offset}";

    private Valid<ShapeRequest> CheckRequest(TenantContext database, CollectionInfo collection)
    {
        if (DocumentRoute.CheckParameters(HttpContext, "where", "handle", "offset", "live", "cursor") is Validation invalid)
        {
            return invalid;
        }

        var query = HttpContext.Request.Query;
        string? Parameter(string name)
        {
            return query.TryGetValue(name, out var value) ? value.ToString() : null;
        }
        var offsetText = Parameter("offset");
        if (offsetText is null || CheckOffset(offsetText) is not Offset offset)
        {
            return BadOffset;
        }

        // The FR-15 canonical form: the identity and the continuation's where are its bytes.
        var filter = ShapeFilter.All;
        if (Parameter("where") is { } where)
        {
            switch (ShapeFilter.Parse(where))
            {
                case Validation error:
                    return error;
                case ShapeFilter parsed:
                    filter = parsed;
                    break;
            }
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

        var cursorText = Parameter("cursor");
        long? cursor = null;
        Resume? resume = null;
        if (isLive)
        {
            if (cursorText is not null)
            {
                if (Decimal(cursorText) is not { } bucket || bucket == long.MaxValue)
                {
                    return new Validation("cursor must be a decimal live bucket below 9223372036854775807");
                }

                cursor = bucket;
            }
        }
        else if (offset is Bootstrap && handle is not null)
        {
            if (cursorText is not null)
            {
                return new Validation("resume cannot carry a handle");
            }
        }
        else if (offset is Bootstrap)
        {
            // TB-Shape-Resume counts only here: offset=-1 without a handle.
            var header = HttpContext.Request.Headers.TryGetValue(ResumeHeader, out var values) ? values : default;
            if (header.Count > 0 && cursorText is not null)
            {
                return new Validation("resume must use one transport");
            }

            if (header.Count > 1)
            {
                return BadResume;
            }

            if ((cursorText ?? (header.Count == 1 ? header.ToString() : null)) is { } token)
            {
                if (CheckResume(token) is not { } decoded)
                {
                    return BadResume;
                }

                resume = decoded;
            }
        }
        else if (cursorText is not null)
        {
            return new Validation("cursor requires live=true or a resume");
        }

        return new ShapeRequest(new ShapeScope(database, collection, filter.Canonical, filter, Bootstrap: offset is Bootstrap), offset, offsetText, handle, isLive, cursor, resume);
    }

    // Canonical decimals: no sign, no leading zeros, at most long.MaxValue.
    private static long? Decimal(string text) =>
        text.Length is > 0 and <= 19 && (text == "0" || text[0] != '0') && text.All(char.IsAsciiDigit)
        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static Offset? CheckOffset(string text)
    {
        if (text == "-1")
        {
            return new Bootstrap();
        }

        if (Decimal(text) is { } at)
        {
            return new DecimalOffset(at);
        }

        var parts = text.Split('.');
        if (parts is not ["snap", var snapshotText, var after] || Decimal(snapshotText) is not { } snapshot)
        {
            return null;
        }

        if (after == "0")
        {
            return new SnapshotOffset(snapshot, null);
        }

        // P is the canonical unpadded base64url of a document id's UTF-8 bytes.
        if (after.Length > Base64Url.GetEncodedLength(256 * 3) || !Base64Url.IsValid(after, out _))
        {
            return null;
        }

        var bytes = Base64Url.DecodeFromChars(after);
        if (Base64Url.EncodeToString(bytes) != after)
        {
            return null;
        }

        try
        {
            return DocumentRoute.CheckStoredId(StrictUtf8.GetString(bytes)) is string id ? new SnapshotOffset(snapshot, id) : null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    // base64url(0x01 ‖ resume_scope16 ‖ R8), canonical, with R8 a changes feed cursor (section 9).
    private static Resume? CheckResume(string token)
    {
        if (token.Length > MaxResume || !Base64Url.IsValid(token, out var length) || length <= 1 + ResumeScopeBytes)
        {
            return null;
        }

        var bytes = Base64Url.DecodeFromChars(token);
        if (bytes[0] != 1 || Base64Url.EncodeToString(bytes) != token)
        {
            return null;
        }

        return ChangeCursor.Decode(Base64Url.EncodeToString(bytes.AsSpan(1 + ResumeScopeBytes))) is { } cursor
            ? new Resume(bytes.AsMemory(1, ResumeScopeBytes), cursor)
            : null;
    }

    private sealed record ShapeRequest(ShapeScope Scope, Offset Offset, string OffsetText, string? Handle, bool Live, long? Cursor, Resume? Resume);

    private sealed record Resume(ReadOnlyMemory<byte> Scope, FeedCursor Cursor);

    private abstract record Offset;

    private sealed record Bootstrap : Offset;

    private sealed record SnapshotOffset(long Snapshot, string? AfterId) : Offset;

    private sealed record DecimalOffset(long Value) : Offset;

    // Writes the body, then releases its admission: the bytes are referenced until the response is sent.
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

    private sealed class UriTooLong : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) =>
            BadRequests.WriteAsync(httpContext, StatusCodes.Status414UriTooLong, "uri_too_long", "the continuation URL would not fit the 8 KiB request line", op: null);
    }
}
