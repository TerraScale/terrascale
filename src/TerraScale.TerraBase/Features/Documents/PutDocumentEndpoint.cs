using System.Text.Json;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary><c>PUT …/documents/{id}</c>: inserts or replaces a JSON document.</summary>
public sealed class PutDocumentEndpoint(IDocumentService documents) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(DocumentRoute.Collection + "/{id}");
        Name("putDocument");
        Summary("Inserts or replaces a JSON document");
        Description("The body is the complete document: a JSON object of at most 1 MiB, 16 levels and 4096 values. Optional If-Match (current version or *) or If-None-Match: * (absent or deleted). Returns 201 when the document was absent or deleted, 200 otherwise; a failed precondition returns 412 precondition_failed.");
        Tags("Documents");
        Metadata(new RequestBodyMetadata(typeof(JsonElement), DocumentRoute.JsonMediaType));
        Produces<WriteBody>(StatusCodes.Status201Created);
        Produces<WriteBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status412PreconditionFailed);
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var key = DocumentRoute.Key(HttpContext);
        DocumentRoute.Parameters(HttpContext);
        DocumentRoute.RejectIdempotencyKey(HttpContext);
        var precondition = ETags.Precondition(HttpContext);
        var json = await DocumentRoute.ReadDocumentAsync(HttpContext, cancellationToken);
        var (body, created) = await DocumentRoute.WriteAsync(
            HttpContext, documents, key, new PutOperation(key.Collection, key.Id, json, precondition), cancellationToken);
        return created ? TypedResults.Created(DocumentRoute.Location(key), body) : TypedResults.Ok(body);
    }
}
