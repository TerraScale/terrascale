using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary><c>DELETE /v1/databases/{database}/collections/{collection}</c>: drops a collection.</summary>
public sealed class DropCollectionEndpoint(IDocumentService documents) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(DocumentRoute.Collections + "/{collection}");
        Name("dropCollection");
        Summary("Drops a collection");
        Description("Deletes the collection and every document in it in one transaction, and returns the dropped collection.");
        Tags("Collections");
        Produces<CollectionBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var database = DocumentRoute.Database(HttpContext);
        var name = DocumentRoute.CollectionName(HttpContext);
        DocumentRoute.Parameters(HttpContext);
        DocumentRoute.RejectIdempotencyKey(HttpContext);
        return TypedResults.Ok(CreateCollectionEndpoint.Body(await documents.DropCollectionAsync(database, name, cancellationToken)));
    }
}
