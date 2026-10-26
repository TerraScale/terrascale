using System.Text.Json;
using Microsoft.OpenApi;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Memory;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary>Reads a bounded page of historical documents against a proposed schema.</summary>
public sealed class PreviewCollectionSchemaEndpoint(IDocumentService documents, ITenantDirectory tenants, IMemoryBudget memory) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(CollectionSchemaRoute.Route + "/preview");
        Name("previewCollectionSchema");
        Summary("Previews a proposed JSON Schema without applying it");
        Description("Project credentials with documents:read and documents:write only. Raw schema body, at most 64 KiB; repeat identical bytes with next_cursor as cursor. Counts are page-only, at most 100 documents/1 MiB (one larger document is allowed). At most 16 SHA-256 ID/path diagnostics, no values. Continuation requires the same process, schema and local database frontier; 409 preview_stale or 400 invalid_cursor requires restarting. An observational local result, not a globally serialized gate; later writes can stale it. Applying remains prospective.");
        Tags("Collections");
        Metadata(new RequestBodyMetadata(typeof(JsonElement), DocumentRoute.JsonMediaType));
        Metadata(new OpenApiParameter
        {
            Name = "cursor",
            In = ParameterLocation.Query,
            Description = "Opaque next_cursor from the preceding page; repeat the same schema on the same node.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 2048 },
        });
        Produces<CollectionSchemaPreviewBody>();
        foreach (var status in new[] { 400, 403, 404, 409, 413, 415, 503 })
        {
            Produces<ErrorBody>(status);
        }

        RequireAuthorization(ApiKeyScopes.DocumentsRead, ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        HttpContext.Features.Get<ProjectAccess>() is { IsEndUser: true }
            ? ValueTask.FromResult(ErrorResponse.From(new Forbidden("project credentials are required")))
            : CollectionSchemaRoute.ExecuteAsync(HttpContext, tenants, (database, name) => PreviewAsync(database, name, cancellationToken), cancellationToken, "cursor");

    private async ValueTask<IResult> PreviewAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        using var admission = memory.TryReserve(MemoryLane.Foreground, 16L * CollectionSchemaValidation.MaxBytes);
        if (admission is null)
        {
            return ErrorResponse.From(new Unavailable("schema preview exceeds the memory budget", "capacity"));
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
                    return await documents.PreviewCollectionSchemaAsync(database, name, schema.RootElement, HttpContext.Request.Query["cursor"].FirstOrDefault(), cancellationToken) switch
                    {
                        CollectionSchemaPreviewBody result => TypedResults.Ok(result),
                        NotFound error => ErrorResponse.From(error),
                        Validation error => ErrorResponse.From(error),
                        Conflict error => ErrorResponse.From(error),
                        Forbidden error => ErrorResponse.From(error),
                        Unavailable error => ErrorResponse.From(error),
                    };
                }
                catch (JsonException)
                {
                    return ErrorResponse.From(new Validation("the schema must be bounded valid JSON without duplicate members", "invalid_schema"));
                }
            default:
                throw new InvalidOperationException("unknown schema preview body result");
        }
    }
}
