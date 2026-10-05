using System.Buffers.Text;
using System.Text;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary><c>GET /v1/projects/{project}/databases/{database}/collections?limit=&amp;cursor=</c>: lists collections in name order.</summary>
public sealed class ListCollectionsEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    /// <inheritdoc />
    public override void Configure()
    {
        Get(DocumentRoute.Collections);
        Name("listCollections");
        Summary("Lists collections in name order");
        Description("Query: limit (1 to 200, default 50) and cursor (next_cursor of the previous page).");
        Tags("Collections");
        Produces<CollectionPageBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var database = await DocumentRoute.TenantAsync(HttpContext, tenants, cancellationToken);
        var query = DocumentRoute.Parameters(HttpContext, "limit", "cursor");
        var after = query.TryGetValue("cursor", out var cursor) ? AfterName(cursor!) : null;
        var (collections, hasMore) = await documents.ListCollectionsAsync(
            database, after, DocumentRoute.Limit(query, DefaultLimit, MaxLimit), cancellationToken);
        return TypedResults.Ok(new CollectionPageBody(
            [.. collections.Select(CreateCollectionEndpoint.Body)],
            hasMore ? Base64Url.EncodeToString(Encoding.UTF8.GetBytes(collections[^1].Name)) : null));
    }

    private static string AfterName(string cursor)
    {
        try
        {
            return DocumentRoute.CollectionName(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)));
        }
        catch (Exception error) when (error is FormatException or ApiException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_cursor", "the cursor is not a collection cursor");
        }
    }
}
