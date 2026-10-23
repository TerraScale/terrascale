using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary>Reads a collection's declared expiry member.</summary>
public sealed class GetCollectionTtlEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(CollectionTtlRoute.Route);
        Name("getCollectionTtl");
        Summary("Reads a collection's TTL");
        Description("{\"path\":…} is the JSON Pointer of the expiry member, or null when documents never expire.");
        Tags("Collections");
        Produces<CollectionTtlBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        CollectionSchemaRoute.ExecuteAsync(HttpContext, tenants,
            async (database, name) => CollectionTtlRoute.Answer(await documents.GetCollectionTtlAsync(database, name, cancellationToken)), cancellationToken);
}
