using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary>Attaches or replaces a collection's declared JSON Schema.</summary>
public sealed class PutCollectionSchemaEndpoint(IDocumentService documents, ITenantDirectory tenants, IMemoryBudget memory) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(CollectionSchemaRoute.Route);
        Name("putDeclaredCollectionSchema");
        Summary("Attaches or replaces a collection's JSON Schema");
        Description("The body is a self-contained Draft 2020-12 JSON Schema, at most 64 KiB. Applies to future local writes; existing documents and committed replication replay are not revalidated. Invalid schemas leave the previous schema in place.");
        Tags("Collections");
        Metadata(new RequestBodyMetadata(typeof(JsonElement), DocumentRoute.JsonMediaType));
        Produces<CollectionSchemaBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<ErrorBody>(StatusCodes.Status415UnsupportedMediaType);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        CollectionSchemaRoute.ExecuteAsync(HttpContext, tenants, (database, name) => PutAsync(database, name, cancellationToken), cancellationToken);

    private async ValueTask<IResult> PutAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        if (DocumentRoute.CheckIdempotencyKey(HttpContext) is Validation invalid)
        {
            return ErrorResponse.From(invalid);
        }

        // Body, parsed trees, compilation and replicated metadata share this operation's admission.
        using var reservation = memory.TryReserve(MemoryLane.Foreground, 16L * CollectionSchemaValidation.MaxBytes);
        if (reservation is null)
        {
            return ErrorResponse.From(new Unavailable("schema admission exceeds the memory budget", "capacity"));
        }

        switch (await DocumentRoute.CheckBodyAsync(HttpContext, CollectionSchemaValidation.MaxBytes, "invalid_schema", cancellationToken, DocumentRoute.JsonMediaType))
        {
            case Validation error:
                return ErrorResponse.From(error);
            case PayloadTooLarge error:
                return ErrorResponse.From(error);
            case UnsupportedMediaType error:
                return ErrorResponse.From(error);
            case byte[] body:
                try
                {
                    using var schema = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = CollectionSchemaValidation.MaxDepth, AllowDuplicateProperties = false });
                    return CollectionSchemaRoute.Answer(await documents.SetCollectionSchemaAsync(database, name, schema.RootElement, cancellationToken));
                }
                catch (JsonException)
                {
                    return ErrorResponse.From(new Validation("the schema must be valid JSON, with at most 15 levels and no duplicate members", "invalid_schema"));
                }
            default:
                throw new InvalidOperationException("unknown schema request body result");
        }
    }
}
