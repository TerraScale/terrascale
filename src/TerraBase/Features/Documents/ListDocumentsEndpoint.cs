using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary><c>GET …/documents?limit=&amp;cursor=</c>: lists a collection in id order.</summary>
public sealed class ListDocumentsEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(DocumentRoute.Collection);
        Name("listDocuments");
        Summary("Lists JSON documents in id order");
        Description("Query: limit (1 to 1000, default 50) and cursor (next_cursor of the previous page). A page also stops at 8 MiB of JSON; next_cursor is null only on the last page.");
        Tags("Documents");
        Produces<DocumentPageBody>();
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
            TenantContext database => (DocumentRoute.CheckCollectionName(HttpContext), DocumentRoute.CheckParameters(HttpContext, "limit", "cursor")) switch
            {
                (Validation error, _) => ErrorResponse.From(error),
                (_, Validation error) => ErrorResponse.From(error),
                (string collection, IQueryCollection query) => await ListAsync(database, collection, query, cancellationToken),
            },
        };

    private async ValueTask<IResult> ListAsync(TenantContext database, string collection, IQueryCollection query, CancellationToken cancellationToken)
    {
        var digest = await CollectionRuleAccess.BindCursorAsync(documents, database, collection, QueryParser.Digest(database, collection, "list"), cancellationToken);
        if (!query.TryGetValue("cursor", out var cursor))
        {
            return await PageAsync(database, collection, digest, query, after: null, cancellationToken);
        }

        return QueryParser.CheckPosition(cursor!, digest, 0) switch
        {
            Validation error => ErrorResponse.From(error),
            QueryPosition after => await PageAsync(database, collection, digest, query, after, cancellationToken),
        };
    }

    private async ValueTask<IResult> PageAsync(
        TenantContext database, string collection, string digest, IQueryCollection query, QueryPosition? after, CancellationToken cancellationToken) =>
        DocumentRoute.CheckLimit(query, QueryParser.DefaultLimit, QueryParser.MaxLimit) switch
        {
            Validation error => ErrorResponse.From(error),
            int limit => await PageAsync(database, collection, digest, after, limit, cancellationToken),
        };

    private async ValueTask<IResult> PageAsync(
        TenantContext database, string collection, string digest, QueryPosition? after, int limit, CancellationToken cancellationToken) =>
        await documents.QueryAsync(
            database, new DocumentQuery(collection, null, [], after, limit, DocumentRoute.MaxPageBytes), countLimit: null, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Validation error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            QueryOutcome outcome => Page(database, collection, digest, outcome.Page, cancellationToken),
        };

    private IResult Page(TenantContext database, string collection, string digest, QueryPage page, CancellationToken cancellationToken)
    {
        string? next = null;
        if (page.HasMore)
        {
            var cursor = QueryParser.CheckCursor(digest, page.Rows[^1]);
            if (cursor is Validation error)
            {
                return ErrorResponse.From(error);
            }

            next = cursor switch { string value => value, Validation => throw new InvalidOperationException("cursor validation was already handled") };
        }

        var body = new DocumentPageBody(Bodies(collection, page, cancellationToken), next);
        documents.AcceptRead(database, collection, page);
        return TypedResults.Ok(body);
    }

    private static List<DocumentBody> Bodies(string collection, QueryPage page, CancellationToken cancellationToken) =>
        [.. page.Rows.Select(row => GetDocumentEndpoint.Body(collection, row.Document, cancellationToken: cancellationToken))];
}
