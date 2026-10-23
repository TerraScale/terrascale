using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary><c>DELETE /v1/projects/{project}/databases/{database}/collections/{collection}</c>: drops a collection.</summary>
public sealed class DropCollectionEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(DocumentRoute.Collections + "/{collection}");
        Name("dropCollection");
        Summary("Drops a collection");
        Description("Applies the collection deletion version to its documents in one transaction and returns the dropped collection. Entries newer than the deletion version survive; the collection remains while newer document state exists.");
        Tags("Collections");
        Produces<CollectionBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DocumentRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => (
                DocumentRoute.CheckCollectionName(HttpContext), DocumentRoute.CheckParameters(HttpContext), DocumentRoute.CheckIdempotencyKey(HttpContext)) switch
            {
                (Validation error, _, _) => ErrorResponse.From(error),
                (_, Validation error, _) => ErrorResponse.From(error),
                (_, _, Validation error) => ErrorResponse.From(error),
                (string name, _, _) => CreateCollectionEndpoint.Answer(
                    await documents.DropCollectionAsync(database, name, cancellationToken), collection => TypedResults.Ok(CreateCollectionEndpoint.Body(collection))),
            },
        };
}
