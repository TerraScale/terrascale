using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary><c>POST …/collections/{collection}/query</c>: filters, sorts and projects documents (query v1).</summary>
public sealed class QueryDocumentsEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    // count examines at most this many documents; beyond, it is a lower bound.
    private const long MaxCount = 100_000;

    /// <inheritdoc />
    public override void Configure()
    {
        Post(DocumentRoute.Collections + "/{collection}/query");
        Name("queryDocuments");
        Summary("Queries JSON documents");
        Description("""
            Filters ({"and"|"or":[F…]} with 1 to 64 children, {"not":F}, {"eq"|"ne"|"lt"|"lte"|"gt"|"gte":[P,V]}, {"in"|"nin":[P,[V…]]} with 1 to 256 values, {"exists":P}, {"prefix":[P,"str"]}, {"contains":[P,V]}, {"search":[P,"terms"]} (requires a text index covering P, nonempty terms up to 4096 UTF-8 bytes, no NUL), {"within":[P,{"lat":…,"lon":…,"meters":M}]} (haversine circle, M up to 20000000) or {"within":[P,{"south":…,"west":…,"north":…,"east":…}]} (box, west > east crosses the antimeridian) matching documents whose P is an object with numeric lat and lon (a btree index on P/lat lets the box seek); at most 8 levels and 256 nodes) run inside Turso. P is a JSON Pointer of object members (nested paths allowed) or "$id"; V is a JSON scalar. Ordered comparisons only match values of V's type and reject null; ne and nin also match missing members. A sole sort {"path":"$score","order":"desc"} requires exactly one search predicate and returns each item's BM25 score, higher first, ties by id ascending; cursors retain score and id. search cannot combine with near. sort has at most 4 {path, order} keys, then id; repeated paths retain only their first occurrence, and keys after the unique $id are omitted; encoded cursors are at most 1 MiB and cursor-producing string values are at most 64 KiB UTF-8 each, and the compact query plus continuation must fit the 4 MiB request limit (400 sort_requires_index beyond); values order missing < null < false < true < numbers < strings < arrays/objects. project keeps at most 64 paths with their parents; duplicate decoded paths and descendants covered by selected ancestors are copied only once, with case-sensitive member names. limit is 1 to 1000 (default 50); a page also stops at 8 MiB of JSON. cursor is the previous page's next_cursor and only works with the same filter, sort and project (400 invalid_cursor). count counts matches among at most 100000 documents (exact is false beyond, and value is a lower bound), on requests without a cursor. Queries and counts are interrupted after 5 million engine work units or a 5-second deadline (400 invalid_query, query too expensive). Cancellation also interrupts Turso. sort [{"near":{"path":P,"vector":[…],"metric":"cosine"|"l2"}}] alone orders by ascending distance (cosine, the default, or l2) to a vector of 1 to 4096 numbers, returned as each item's distance; it matches only documents whose P is a numeric array of the same length, combines with filter, and returns one page of at most limit 1000 documents without next_cursor. A vector index (kind vector) on P with the vector's length makes it approximate: the 200 (or 4×limit) documents whose sign bits are closest, 4096 with a filter, are reranked exactly; filters then apply to those candidates only. Without one, while it builds, or for owner-scoped reads, it scans the collection. A field sort requires at most 10000 live candidate documents and at most 8 MiB of candidate JSON, otherwise 400 sort_requires_index. sort [{"near":{"path":P,"lat":…,"lon":…}}] orders the same way by meters to that point from P's {lat, lon}, returned as distance. aggregate {"group_by":[P…],"sum"|"avg"|"min"|"max":[P…]} (0 to 4 group_by paths, at most 16 summary paths, no sort, project, cursor or count) returns groups instead of items: per group its key (one JSON value per group_by path; missing groups as null), its count and the summaries over numbers only (sum 0, others null without any), in key order; limit bounds the groups (400 invalid_query beyond) and the work budget applies. explain true also returns explain: the engine plan steps, the indexes used, full_scan, rows_scanned (candidate documents visited), work_units (against the 5 million budget), elapsed_ms, and on a full scan a suggested_index of the first filtered field (equalities first) for POST …/indexes; it bypasses the result cache. lower is not supported yet.
            """);
        Tags("Queries");
        Metadata(new RequestBodyMetadata(typeof(QueryRequest), DocumentRoute.JsonMediaType));
        Produces<DocumentPageBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Produces<ErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<ErrorBody>(StatusCodes.Status415UnsupportedMediaType);
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DocumentRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => (DocumentRoute.CheckCollectionName(HttpContext), DocumentRoute.CheckParameters(HttpContext)) switch
            {
                (Validation error, _) => ErrorResponse.From(error),
                (_, Validation error) => ErrorResponse.From(error),
                (string collection, _) => await DocumentRoute.CheckBodyAsync(
                    HttpContext, DocumentRoute.MaxRequestBytes, "invalid_query", cancellationToken, DocumentRoute.JsonMediaType) switch
                {
                    Validation error => ErrorResponse.From(error),
                    PayloadTooLarge error => ErrorResponse.From(error),
                    UnsupportedMediaType error => ErrorResponse.From(error),
                    byte[] body => RequestJson.Read<QueryRequest>(body, "invalid_query") switch
                    {
                        Validation error => ErrorResponse.From(error),
                        QueryRequest request => await QueryAsync(database, collection, body, request, cancellationToken),
                    },
                },
            },
        };

    private async ValueTask<IResult> QueryAsync(TenantContext database, string collection, byte[] body, QueryRequest request, CancellationToken cancellationToken)
    {
        var checkedQuery = QueryParser.CheckParse(database, collection, request);
        if (checkedQuery is Validation invalid)
        {
            return ErrorResponse.From(invalid);
        }

        var parsed = checkedQuery switch
        {
            ParsedQuery value => value,
            Validation => throw new InvalidOperationException("query validation was already handled"),
        };
        var query = parsed with { Digest = await CollectionRuleAccess.BindCursorAsync(documents, database, collection, parsed.Digest, cancellationToken) };
        return request.Cursor is not { } cursor
            ? await PageAsync(database, collection, body, request, query, after: null, cancellationToken)
            : QueryParser.CheckPosition(cursor, query.Digest, query.Sort.Count) switch
            {
                Validation error => ErrorResponse.From(error),
                QueryPosition after => await PageAsync(database, collection, body, request, query, after, cancellationToken),
            };
    }

    private async ValueTask<IResult> PageAsync(
        TenantContext database, string collection, byte[] body, QueryRequest request, ParsedQuery query, QueryPosition? after, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        return await documents.QueryAsync(
            database,
            new DocumentQuery(collection, query.Filter, query.Sort, after, query.Limit, DocumentRoute.MaxPageBytes, query.Near, query.Aggregate, query.Explain),
            request.Count == true && after is null ? MaxCount : null,
            cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Validation error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            QueryOutcome outcome => Page(collection, body, query, outcome.Page, outcome.Count, Stopwatch.GetElapsedTime(started), cancellationToken),
        };
    }

    private static IResult Page(
        string collection, byte[] body, ParsedQuery query, QueryPage page, (long Count, bool Complete)? count, TimeSpan elapsed, CancellationToken cancellationToken)
    {
        if (!page.HasMore)
        {
            return TypedResults.Ok(Body(collection, query, page, count, next: null, cancellationToken) with { Explain = Explain(query, page.Plan, elapsed) });
        }

        return QueryParser.CheckCursor(query.Digest, page.Rows[^1], body) switch
        {
            Validation error => ErrorResponse.From(error),
            string next => TypedResults.Ok(Body(collection, query, page, count, next, cancellationToken) with { Explain = Explain(query, page.Plan, elapsed) }),
        };
    }

    private static DocumentPageBody Body(
        string collection, ParsedQuery query, QueryPage page, (long Count, bool Complete)? count, string? next, CancellationToken cancellationToken) =>
        new(
            [.. page.Rows.Select(row => GetDocumentEndpoint.Body(collection, row.Document, query.Project, cancellationToken) with
            {
                Distance = query.Near is null ? null : row.Keys[0] as double?,
                Score = query.Sort is [{ Score: true }] ? Convert.ToDouble(row.Keys[1], CultureInfo.InvariantCulture) : null,
            })],
            next,
            count is var (n, complete) ? new CountBody(n.ToString(CultureInfo.InvariantCulture), complete) : null,
            Groups(query, page));

    /// <summary>How the query ran, with an index suggestion when it read the whole collection.</summary>
    internal static ExplainBody? Explain(ParsedQuery query, DocumentQueryPlan? plan, TimeSpan elapsed)
    {
        if (plan is null)
        {
            return null;
        }

        // Turso reports walking a whole table or index as "SCAN …" (also "SCAN items USING INDEX" of the id order) and a
        // seek as "SEARCH … USING …"; per-row state lookups are seeks.
        var fullScan = plan.Steps.Any(step => step.StartsWith("SCAN ", StringComparison.Ordinal));
        return new ExplainBody(plan.Steps, plan.Indexes, fullScan, plan.RowsScanned, plan.WorkUnits, Math.Round(elapsed.TotalMilliseconds, 3),
            fullScan && IndexFields(query.Filter) is { Count: > 0 } fields ? fields : null);
    }

    // The field an index could seek: the first top-level equality, else range, as POST …/indexes takes it. One field,
    // because Turso's planner seeks a single-key expression index but walks the id order for a compound one.
    private static List<string> IndexFields(DocumentFilter? filter)
    {
        var terms = filter is AndFilter and ? and.Children : filter is null ? [] : [filter];
        var paths = terms.Select<DocumentFilter, (int Rank, DocumentPath Path)>(term => term switch
        {
            CompareFilter { Op: Comparison.Equal } equal => (0, equal.Path),
            InFilter inside => (0, inside.Path),
            CompareFilter range => (1, range.Path),
            PrefixFilter prefix => (1, prefix.Path),
            WithinFilter within when !within.Path.IsId => (1, new DocumentPath([.. within.Path.Segments!, "lat"])),
            _ => (2, DocumentPath.Id),
        }).Where(term => term.Rank < 2 && !term.Path.IsId).OrderBy(term => term.Rank);
        var fields = new List<string>();
        foreach (var (_, path) in paths)
        {
            if (path.Segments!.All(segment => !segment.Contains('.', StringComparison.Ordinal))
                && DocumentField.TryParse(string.Join('.', path.Segments!), out var field) && fields.Count == 0)
            {
                fields.Add(field.Path);
            }
        }

        return fields;
    }

    /// <summary>An aggregate's groups as response bodies; <see langword="null"/> for a document query.</summary>
    internal static IReadOnlyList<GroupBody>? Groups(ParsedQuery query, QueryPage page) => query.Aggregate is not { } aggregate || page.Groups is not { } groups
        ? null
        : [.. groups.Select(group =>
        {
            Dictionary<string, double?>? Summary(string op)
            {
                return aggregate.Values.Any(value => value.Op == op)
                    ? aggregate.Values.Select((value, i) => (value, i)).Where(item => item.value.Op == op).ToDictionary(
                        item => Pointer(item.value.Path), item => group.Values[item.i] is double number && double.IsFinite(number) ? number : (double?)null)
                    : null;
            }

            return new GroupBody([.. group.Key.Select(Key)], group.Count.ToString(CultureInfo.InvariantCulture), Summary("sum"), Summary("avg"), Summary("min"), Summary("max"));
        })];

    private static string Pointer(DocumentPath path) =>
        string.Concat(path.Segments!.Select(segment => "/" + segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));

    // Ranks as TursoQueryCompiler.CompileAggregate reports them: arrays and objects arrive as their JSON text.
    private static JsonElement Key((long Rank, object? Value) key)
    {
        if (key is (6, string json))
        {
            return JsonElement.Parse(json);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            switch (key)
            {
                case (2 or 3, _):
                    writer.WriteBooleanValue(key.Rank == 3);
                    break;
                case (4, long integer):
                    writer.WriteNumberValue(integer);
                    break;
                case (4, double real):
                    writer.WriteNumberValue(real);
                    break;
                case (5, string text):
                    writer.WriteStringValue(text);
                    break;
                default:
                    writer.WriteNullValue();
                    break;
            }
        }

        return JsonElement.Parse(buffer.WrittenSpan);
    }
}
