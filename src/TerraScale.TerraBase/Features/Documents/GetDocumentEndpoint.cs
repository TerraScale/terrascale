using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary><c>GET …/documents/{id}</c>: reads one JSON document.</summary>
public sealed class GetDocumentEndpoint(IDocumentService documents) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(DocumentRoute.Collection + "/{id}");
        Name("getDocument");
        Summary("Reads a JSON document");
        Tags("Documents");
        Produces<DocumentBody>();
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await documents.GetAsync(DocumentRoute.Key(HttpContext), cancellationToken) is { } document
            ? TypedResults.Ok(DocumentBody.From(document))
            : TypedResults.NotFound();
}
