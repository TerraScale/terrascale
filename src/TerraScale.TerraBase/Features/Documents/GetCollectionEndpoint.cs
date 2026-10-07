using TerraScale.Endpoints;
using TerraScale.TerraBase.Errors;
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
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DocumentRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            TenantContext database => (DocumentRoute.CheckCollectionName(HttpContext), DocumentRoute.CheckParameters(HttpContext)) switch
            {
                (Validation error, _) => ErrorResponse.From(error),
                (_, Validation error) => ErrorResponse.From(error),
                (string name, _) => CreateCollectionEndpoint.Answer(
                    await documents.GetCollectionAsync(database, name, cancellationToken), collection => TypedResults.Ok(CreateCollectionEndpoint.Body(collection))),
            },
        };
}
