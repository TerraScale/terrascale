using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary>Reads the declared JSON Schema of a collection.</summary>
public sealed class GetCollectionSchemaEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(CollectionSchemaRoute.Route);
        Name("getDeclaredCollectionSchema");
        Summary("Reads a collection's declared JSON Schema");
        Tags("Collections");
        Produces<CollectionSchemaBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        CollectionSchemaRoute.ExecuteAsync(HttpContext, tenants,
            async (database, name) => CollectionSchemaRoute.Answer(await documents.GetCollectionSchemaAsync(database, name, cancellationToken)), cancellationToken);
}
