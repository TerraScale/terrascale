using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary>Detaches a collection's declared JSON Schema without deleting documents.</summary>
public sealed class DeleteCollectionSchemaEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(CollectionSchemaRoute.Route);
        Name("deleteDeclaredCollectionSchema");
        Summary("Disables optional schema validation for a collection");
        Tags("Collections");
        Produces<CollectionSchemaBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        CollectionSchemaRoute.ExecuteAsync(HttpContext, tenants, async (database, name) =>
            DocumentRoute.CheckIdempotencyKey(HttpContext) switch
            {
                Validation error => ErrorResponse.From(error),
                Done => CollectionSchemaRoute.Answer(await documents.SetCollectionSchemaAsync(database, name, null, cancellationToken)),
            }, cancellationToken);
}
