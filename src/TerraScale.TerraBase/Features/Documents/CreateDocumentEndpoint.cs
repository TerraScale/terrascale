using System.Text.Json;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary><c>POST …/documents</c>: creates a JSON document with a server-generated UUIDv7 id.</summary>
public sealed class CreateDocumentEndpoint(IDocumentService documents) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(DocumentRoute.Collection);
        Name("createDocument");
        Summary("Creates a JSON document with a generated id");
        Description("The body is a JSON object of at most 1 MiB. Conditional headers use the same strict parser as PUT: malformed headers return 400; If-Match fails with 412 for the generated absent id; If-None-Match: * permits creation. Returns 201 with Location and the new id, a lowercase UUIDv7.");
        Tags("Documents");
        Metadata(new RequestBodyMetadata(typeof(JsonElement), DocumentRoute.JsonMediaType));
        Produces<WriteBody>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status412PreconditionFailed);
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var key = new DocumentKey(
            DocumentRoute.Database(HttpContext), DocumentRoute.CollectionName(HttpContext), Guid.CreateVersion7().ToString("D"));
        DocumentRoute.Parameters(HttpContext);
        DocumentRoute.RejectIdempotencyKey(HttpContext);
        var precondition = ETags.Precondition(HttpContext) with { IfNoneMatch = true };

        var json = await DocumentRoute.ReadDocumentAsync(HttpContext, cancellationToken);
        var (body, _) = await DocumentRoute.WriteAsync(
            HttpContext, documents, key, new PutOperation(key.Collection, key.Id, json, precondition), cancellationToken);
        return TypedResults.Created(DocumentRoute.Location(key), body);
    }
}
