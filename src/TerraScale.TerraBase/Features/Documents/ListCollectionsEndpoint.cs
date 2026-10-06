using System.Buffers.Text;
using System.Text;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Errors;
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
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DocumentRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            TenantContext database => DocumentRoute.CheckParameters(HttpContext, "limit", "cursor") switch
            {
                Validation error => ErrorResponse.From(error),
                IQueryCollection query => (Cursor(query), DocumentRoute.CheckLimit(query, DefaultLimit, MaxLimit)) switch
                {
                    (Validation error, _) => ErrorResponse.From(error),
                    (_, Validation error) => ErrorResponse.From(error),
                    (CollectionCursor cursor, int limit) => await ListAsync(database, cursor.After, limit, cancellationToken),
                },
            },
        };

    private async ValueTask<IResult> ListAsync(TenantContext database, string? after, int limit, CancellationToken cancellationToken)
    {
        var (collections, hasMore) = await documents.ListCollectionsAsync(database, after, limit, cancellationToken);
        return TypedResults.Ok(new CollectionPageBody(
            [.. collections.Select(CreateCollectionEndpoint.Body)],
            hasMore ? Base64Url.EncodeToString(Encoding.UTF8.GetBytes(collections[^1].Name)) : null));
    }

    private static Valid<CollectionCursor> Cursor(IQueryCollection query)
    {
        if (!query.TryGetValue("cursor", out var cursor))
        {
            return new CollectionCursor(null);
        }

        try
        {
            if (DocumentRoute.CheckCollectionName(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor.ToString()))) is string name)
            {
                return new CollectionCursor(name);
            }
        }
        catch (FormatException)
        {
            // Not base64url, or not UTF-8: the same answer as a cursor that names no collection.
        }

        return new Validation("the cursor is not a collection cursor", "invalid_cursor");
    }
}

/// <summary>Where a collection page starts.</summary>
/// <param name="After">The last collection of the previous page; <see langword="null"/> for the first page.</param>
internal sealed record CollectionCursor(string? After);
