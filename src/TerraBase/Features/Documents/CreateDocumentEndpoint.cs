using System.Text.Json;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary><c>POST …/documents</c>: creates a JSON document with a server-generated ULID id.</summary>
public sealed class CreateDocumentEndpoint(IDocumentService documents, ITenantDirectory tenants, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(DocumentRoute.Collection);
        Name("createDocument");
        Summary("Creates a JSON document with a generated id");
        Description("The body is a JSON object of at most 1 MiB. Conditional headers use the same strict parser as PUT: malformed headers return 400; If-Match fails with 412 for the generated absent id; If-None-Match: * permits creation. Returns 201 with Location and the new id, a canonical uppercase ULID. " + DocumentRoute.WriteDurabilityDescription);
        Tags("Documents");
        Metadata(new RequestBodyMetadata(typeof(JsonElement), DocumentRoute.JsonMediaType));
        Produces<WriteBody>(StatusCodes.Status201Created);
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
        await DocumentRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => (
                DocumentRoute.CheckCollectionName(HttpContext),
                DocumentRoute.CheckParameters(HttpContext),
                DocumentRoute.CheckIdempotencyKey(HttpContext),
                ETags.CheckPrecondition(HttpContext)) switch
            {
                (Validation error, _, _, _) => ErrorResponse.From(error),
                (_, Validation error, _, _) => ErrorResponse.From(error),
                (_, _, Validation error, _) => ErrorResponse.From(error),
                (_, _, _, Validation error) => ErrorResponse.From(error),
                (string collection, _, _, Precondition precondition) => await DocumentRoute.CheckDocumentAsync(HttpContext, cancellationToken) switch
                {
                    Validation error => ErrorResponse.From(error),
                    PayloadTooLarge error => ErrorResponse.From(error),
                    UnsupportedMediaType error => ErrorResponse.From(error),
                    byte[] json => await CreateAsync(
                        new DocumentKey(database, collection, EntityIds.New(time).ToString()), json, precondition with { IfNoneMatch = true }, cancellationToken),
                },
            },
        };

    private async ValueTask<IResult> CreateAsync(DocumentKey key, byte[] json, Precondition precondition, CancellationToken cancellationToken)
    {
        return await DocumentRoute.WriteAsync(
            HttpContext, documents, key, new PutOperation(key.Collection, key.Id, json, precondition),
            (body, _) => TypedResults.Created(DocumentRoute.Location(HttpContext, key), body), cancellationToken);
    }
}
