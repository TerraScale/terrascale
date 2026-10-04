using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary><c>GET /v1/databases/{database}/collections/{collection}</c>: reads a collection.</summary>
public sealed class GetCollectionEndpoint(IDocumentService documents) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(DocumentRoute.Collections + "/{collection}");
        Name("getCollection");
        Summary("Reads a collection");
        Tags("Collections");
        Produces<CollectionBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var database = DocumentRoute.Database(HttpContext);
        var name = DocumentRoute.CollectionName(HttpContext);
        DocumentRoute.Parameters(HttpContext);
        return TypedResults.Ok(CreateCollectionEndpoint.Body(await documents.GetCollectionAsync(database, name, cancellationToken)));
    }
}
