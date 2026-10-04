using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary><c>DELETE …/documents/{id}</c>: deletes one JSON document.</summary>
public sealed class DeleteDocumentEndpoint(IDocumentService documents) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(DocumentRoute.Collection + "/{id}");
        Name("deleteDocument");
        Summary("Deletes a JSON document");
        Tags("Documents");
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await documents.DeleteAsync(DocumentRoute.Key(HttpContext), cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
}
