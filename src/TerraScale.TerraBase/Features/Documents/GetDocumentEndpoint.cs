using System.Text.Json;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary><c>GET …/documents/{id}</c>: reads one JSON document.</summary>
public sealed class GetDocumentEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(DocumentRoute.Collection + "/{id}");
        Name("getDocument");
        Summary("Reads a JSON document");
        Description("Sends the document state as a strong ETag, including the persisted database incarnation, merge version, JSON and deletion state. Equal-version replicated body changes get distinct validators. If-None-Match with the current version returns 304. A deleted document returns 404 not_found with its tombstone's ETag, so it can be recreated with If-Match.");
        Tags("Documents");
        Produces<DocumentBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var key = await DocumentRoute.KeyAsync(HttpContext, tenants, cancellationToken);
        DocumentRoute.Parameters(HttpContext);
        var document = await documents.GetAsync(key, cancellationToken);
        var version = document is null ? null : ETags.Version(document.Incarnation, key.Collection, document.Id, document.Version, document.Json, document.Deleted);
        if (version is not null)
        {
            // A deleted document's 404 carries its tombstone's version too.
            ETags.Set(HttpContext, version);
        }

        if (ETags.NotModified(HttpContext, document is { Deleted: false }, version))
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        return document is { Deleted: false }
            ? TypedResults.Ok(Body(key.Collection, document, cancellationToken: cancellationToken))
            // The error path clears the prepared response, so the tombstone's version travels with the error.
            : throw new ApiException(StatusCodes.Status404NotFound, "not_found", "the document does not exist") { ETag = version };
    }

    internal static DocumentBody Body(
        string collection, StoredDocument document, IReadOnlyList<DocumentPath>? project = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var json = JsonDocument.Parse(document.Json, RequestJson.StoredDocumentOptions);
        var body = project is null ? json.RootElement.Clone() : Projection.Apply(json.RootElement, project, cancellationToken);
        return new DocumentBody(document.Id, ETags.Version(document.Incarnation, collection, document.Id, document.Version, document.Json), "value", body);
    }
}
