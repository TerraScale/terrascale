using System.Text.Json;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary><c>PUT …/documents/by-key?id=</c>: inserts or replaces a JSON document.</summary>
public sealed class PutDocumentEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(DocumentRoute.Document);
        Name("putDocument");
        Summary("Inserts or replaces a JSON document");
        Description("The body is the complete document: a JSON object of at most 1 MiB, 16 levels and 4096 values. Optional If-Match (current version or *) or If-None-Match: * (absent or deleted). Returns 201 when the document was absent or deleted, 200 otherwise; a failed precondition returns 412 precondition_failed. " + DocumentRoute.WriteDurabilityDescription);
        Tags("Documents");
        Metadata(new RequestBodyMetadata(typeof(JsonElement), DocumentRoute.JsonMediaType));
        Produces<WriteBody>(StatusCodes.Status201Created);
        Produces<WriteBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Produces<ErrorBody>(StatusCodes.Status412PreconditionFailed);
        Produces<ErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<ErrorBody>(StatusCodes.Status415UnsupportedMediaType);
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DocumentRoute.CheckKeyAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            DocumentKey key => (DocumentRoute.CheckParameters(HttpContext, "id"), DocumentRoute.CheckIdempotencyKey(HttpContext), ETags.CheckPrecondition(HttpContext)) switch
            {
                (Validation error, _, _) => ErrorResponse.From(error),
                (_, Validation error, _) => ErrorResponse.From(error),
                (_, _, Validation error) => ErrorResponse.From(error),
                (_, _, Precondition precondition) => await DocumentRoute.CheckDocumentAsync(HttpContext, cancellationToken) switch
                {
                    Validation error => ErrorResponse.From(error),
                    PayloadTooLarge error => ErrorResponse.From(error),
                    UnsupportedMediaType error => ErrorResponse.From(error),
                    byte[] json => await PutAsync(key, json, precondition, cancellationToken),
                },
            },
        };

    private async ValueTask<IResult> PutAsync(DocumentKey key, byte[] json, Precondition precondition, CancellationToken cancellationToken)
    {
        return await DocumentRoute.WriteAsync(
            HttpContext, documents, key, new PutOperation(key.Collection, key.Id, json, precondition),
            (body, created) => created ? TypedResults.Created(DocumentRoute.Location(HttpContext, key), body) : TypedResults.Ok(body), cancellationToken);
    }
}
