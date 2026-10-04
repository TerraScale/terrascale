using TerraScale.Endpoints;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary><c>GET …/documents?limit=&amp;cursor=</c>: lists a collection in id order.</summary>
public sealed class ListDocumentsEndpoint(IDocumentService documents) : EndpointWithoutRequest<IResult>
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
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var database = DocumentRoute.Database(HttpContext);
        var collection = DocumentRoute.CollectionName(HttpContext);
        var query = DocumentRoute.Parameters(HttpContext, "limit", "cursor");
        var digest = QueryParser.Digest(database, collection, "list");
        var after = query.TryGetValue("cursor", out var cursor) ? QueryParser.Position(cursor!, digest, 0) : null;
        var (page, _) = await documents.QueryAsync(
            database,
            new DocumentQuery(collection, null, [], after, DocumentRoute.Limit(query, QueryParser.DefaultLimit, QueryParser.MaxLimit), DocumentRoute.MaxPageBytes),
            countLimit: null,
            cancellationToken);
        return TypedResults.Ok(new DocumentPageBody(
            [.. page.Rows.Select(row => GetDocumentEndpoint.Body(collection, row.Document, cancellationToken: cancellationToken))],
            page.HasMore ? QueryParser.Cursor(digest, page.Rows[^1]) : null));
    }
}
