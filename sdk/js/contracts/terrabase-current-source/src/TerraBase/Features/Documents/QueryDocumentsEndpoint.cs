using System.Globalization;
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
            Filters ({"and"|"or":[F…]} with 1 to 64 children, {"not":F}, {"eq"|"ne"|"lt"|"lte"|"gt"|"gte":[P,V]}, {"in"|"nin":[P,[V…]]} with 1 to 256 values, {"exists":P}, {"prefix":[P,"str"]}, {"contains":[P,V]}; at most 8 levels and 256 nodes) run inside Turso. P is a JSON Pointer of object members (nested paths allowed) or "$id"; V is a JSON scalar. Ordered comparisons only match values of V's type and reject null; ne and nin also match missing members. sort has at most 4 {path, order} keys, then id; repeated paths retain only their first occurrence, and keys after the unique $id are omitted; encoded cursors are at most 1 MiB and cursor-producing string values are at most 64 KiB UTF-8 each, and the compact query plus continuation must fit the 4 MiB request limit (400 sort_requires_index beyond); values order missing < null < false < true < numbers < strings < arrays/objects. project keeps at most 64 paths with their parents; duplicate decoded paths and descendants covered by selected ancestors are copied only once, with case-sensitive member names. limit is 1 to 1000 (default 50); a page also stops at 8 MiB of JSON. cursor is the previous page's next_cursor and only works with the same filter, sort and project (400 invalid_cursor). count counts matches among at most 100000 documents (exact is false beyond, and value is a lower bound), on requests without a cursor. Queries and counts are interrupted after 5 million engine work units or a 5-second deadline (400 invalid_query, query too expensive). Cancellation also interrupts Turso. sort [{"near":{"path":P,"vector":[…],"metric":"cosine"|"l2"}}] alone orders brute force by ascending distance (cosine, the default, or l2) to a vector of 1 to 4096 numbers, returned as each item's distance; it matches only documents whose P is a numeric array of the same length, combines with filter, and returns one page of at most limit 100 documents without next_cursor. A field sort requires at most 10000 live candidate documents and at most 8 MiB of candidate JSON, otherwise 400 sort_requires_index. lower and explain are not supported yet.
            """);
        Tags("Queries");
        Metadata(new RequestBodyMetadata(typeof(QueryRequest), DocumentRoute.JsonMediaType));
        Produces<DocumentPageBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
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

    private async ValueTask<IResult> QueryAsync(TenantContext database, string collection, byte[] body, QueryRequest request, CancellationToken cancellationToken) =>
        QueryParser.CheckParse(database, collection, request) switch
        {
            Validation error => ErrorResponse.From(error),
            ParsedQuery query => request.Cursor is not { } cursor
                ? await PageAsync(database, collection, body, request, query, after: null, cancellationToken)
                : QueryParser.CheckPosition(cursor, query.Digest, query.Sort.Count) switch
                {
                    Validation error => ErrorResponse.From(error),
                    QueryPosition after => await PageAsync(database, collection, body, request, query, after, cancellationToken),
                },
        };

    private async ValueTask<IResult> PageAsync(
        TenantContext database, string collection, byte[] body, QueryRequest request, ParsedQuery query, QueryPosition? after, CancellationToken cancellationToken) =>
        await documents.QueryAsync(
            database,
            new DocumentQuery(collection, query.Filter, query.Sort, after, query.Limit, DocumentRoute.MaxPageBytes, query.Near),
            request.Count == true && after is null ? MaxCount : null,
            cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Validation error => ErrorResponse.From(error),
            QueryOutcome outcome => Page(collection, body, query, outcome.Page, outcome.Count, cancellationToken),
        };

    private static IResult Page(
        string collection, byte[] body, ParsedQuery query, QueryPage page, (long Count, bool Complete)? count, CancellationToken cancellationToken)
    {
        if (!page.HasMore)
        {
            return TypedResults.Ok(Body(collection, query, page, count, next: null, cancellationToken));
        }

        return QueryParser.CheckCursor(query.Digest, page.Rows[^1], body) switch
        {
            Validation error => ErrorResponse.From(error),
            string next => TypedResults.Ok(Body(collection, query, page, count, next, cancellationToken)),
        };
    }

    private static DocumentPageBody Body(
        string collection, ParsedQuery query, QueryPage page, (long Count, bool Complete)? count, string? next, CancellationToken cancellationToken) =>
        new(
            [.. page.Rows.Select(row => GetDocumentEndpoint.Body(collection, row.Document, query.Project, cancellationToken) with
            {
                Distance = query.Near is null ? null : row.Keys[0] as double?,
            })],
            next,
            count is var (n, complete) ? new CountBody(n.ToString(CultureInfo.InvariantCulture), complete) : null);
}
