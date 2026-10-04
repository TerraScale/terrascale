using System.Globalization;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>One page of documents.</summary>
/// <param name="Documents">Documents in id order.</param>
/// <param name="Next">Cursor for the next page (<c>after</c>); <see langword="null"/> on the last page.</param>
public sealed record DocumentPageBody(IReadOnlyList<DocumentBody> Documents, string? Next);

/// <summary><c>GET …/documents?after=&amp;limit=</c>: lists a collection in id order.</summary>
public sealed class ListDocumentsEndpoint(IDocumentService documents) : EndpointWithoutRequest<IResult>
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    /// <inheritdoc />
    public override void Configure()
    {
        Get(DocumentRoute.Collection);
        Name("listDocuments");
        Summary("Lists JSON documents in id order");
        Description("Query: after (exclusive id cursor from the previous page's next), limit (1 to 100, default 20).");
        Tags("Documents");
        Produces<DocumentPageBody>();
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var query = HttpContext.Request.Query;
        var limit = DefaultLimit;
        if (query.TryGetValue("limit", out var rawLimit)
            && (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > MaxLimit))
        {
            throw new BadHttpRequestException("limit must be an integer between 1 and 100");
        }

        var after = query.TryGetValue("after", out var rawAfter) ? rawAfter.ToString() : null;
        var page = await documents.ListAsync(
            DocumentRoute.Database(HttpContext), DocumentRoute.CollectionName(HttpContext), after, limit, cancellationToken);
        if (page is null)
        {
            return TypedResults.NotFound();
        }

        var next = page.Count == limit ? page[^1].Id : null;
        return TypedResults.Ok(new DocumentPageBody([.. page.Select(DocumentBody.From)], next));
    }
}
