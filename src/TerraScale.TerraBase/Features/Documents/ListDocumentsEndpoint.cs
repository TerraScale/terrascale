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

    // Bounds the JSON one page materializes; a page always holds at least one document.
    private const long MaxPageBytes = 8 * 1024 * 1024;

    /// <inheritdoc />
    public override void Configure()
    {
        Get(DocumentRoute.Collection);
        Name("listDocuments");
        Summary("Lists JSON documents in id order");
        Description("Query: after (exclusive id cursor from the previous page's next), limit (1 to 100, default 20). A page also stops at 8 MiB of JSON; next is null only on the last page.");
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
            DocumentRoute.Database(HttpContext), DocumentRoute.CollectionName(HttpContext), after, limit, MaxPageBytes, cancellationToken);
        if (page is null)
        {
            return TypedResults.NotFound();
        }

        var next = page.HasMore ? page.Documents[^1].Id : null;
        return TypedResults.Ok(new DocumentPageBody([.. page.Documents.Select(DocumentBody.From)], next));
    }
}
