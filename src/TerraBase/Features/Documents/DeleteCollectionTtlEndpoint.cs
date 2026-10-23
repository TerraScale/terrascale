using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary>Clears a collection's expiry member, so its documents no longer expire.</summary>
public sealed class DeleteCollectionTtlEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(CollectionTtlRoute.Route);
        Name("deleteCollectionTtl");
        Summary("Stops a collection's documents from expiring");
        Tags("Collections");
        Produces<CollectionTtlBody>();
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
                Done => CollectionTtlRoute.Answer(await documents.SetCollectionTtlAsync(database, name, null, cancellationToken)),
            }, cancellationToken);
}
