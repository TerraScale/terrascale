using TerraScale.Endpoints;
using TerraScale.TerraBase.Errors;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary><c>DELETE …/documents/{id}</c>: deletes one JSON document.</summary>
public sealed class DeleteDocumentEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(DocumentRoute.Collection + "/{id}");
        Name("deleteDocument");
        Summary("Deletes a JSON document");
        Description("Optional If-Match. Returns the tombstone's version; deleting an absent or deleted document returns 404 not_found.");
        Tags("Documents");
        Produces<WriteBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status412PreconditionFailed);
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DocumentRoute.CheckKeyAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            DocumentKey key => (DocumentRoute.CheckParameters(HttpContext), DocumentRoute.CheckIdempotencyKey(HttpContext), ETags.CheckPrecondition(HttpContext)) switch
            {
                (Validation error, _, _) => ErrorResponse.From(error),
                (_, Validation error, _) => ErrorResponse.From(error),
                (_, _, Validation error) => ErrorResponse.From(error),
                (_, _, Precondition precondition) => TypedResults.Ok((await DocumentRoute.WriteAsync(
                    HttpContext, documents, key, new DeleteOperation(key.Collection, key.Id, precondition), cancellationToken)).Body),
            },
        };
}
