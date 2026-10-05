using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary><c>GET /v1/projects/{project}/databases/{database}/collections/{collection}</c>: reads a collection.</summary>
public sealed class GetCollectionEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
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
        var database = await DocumentRoute.TenantAsync(HttpContext, tenants, cancellationToken);
        var name = DocumentRoute.CollectionName(HttpContext);
        DocumentRoute.Parameters(HttpContext);
        return TypedResults.Ok(CreateCollectionEndpoint.Body(await documents.GetCollectionAsync(database, name, cancellationToken)));
    }
}
