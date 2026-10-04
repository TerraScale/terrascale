using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>Result of a write.</summary>
/// <param name="Id">Document id.</param>
/// <param name="Version">The new version.</param>
public sealed record DocumentVersionBody(string Id, long Version);

/// <summary><c>PUT …/documents/{id}</c>: inserts or replaces a JSON document.</summary>
public sealed class PutDocumentEndpoint(IDocumentService documents) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(DocumentRoute.Collection + "/{id}");
        Name("putDocument");
        Summary("Inserts or replaces a JSON document");
        Description("The body is any JSON object of at most 1 MiB.");
        Tags("Documents");
        // Not Accepts<JsonElement>(): TerraScale.Endpoints 3.0.23-rc.1 drops its type on an endpoint
        // without a request model, and the untyped body fails the whole OpenAPI document.
        Metadata(new AcceptsMetadata(["application/json"], typeof(JsonElement)));
        Produces<DocumentVersionBody>();
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var key = DocumentRoute.Key(HttpContext);
        var json = await DocumentRoute.ReadObjectAsync(HttpContext, cancellationToken);
        return await documents.PutAsync(key, json, cancellationToken) is { } version
            ? TypedResults.Ok(new DocumentVersionBody(key.Id, version))
            : TypedResults.NotFound();
    }
}
