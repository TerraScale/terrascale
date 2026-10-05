using TerraScale.Endpoints;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary><c>POST /v1/projects/{project}/databases/{database}/collections</c>: creates a collection.</summary>
public sealed class CreateCollectionEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(DocumentRoute.Collections);
        Name("createCollection");
        Summary("Creates a collection");
        Description("Body {\"name\":…}; names match ^[a-z][a-z0-9_-]{0,62}$. Collections are schemaless; schema, ttl_path, unique, indexes and advisor are not supported yet and fail as unknown members. An existing name fails with 409 name_taken.");
        Tags("Collections");
        Metadata(new RequestBodyMetadata(typeof(CreateCollectionRequest), DocumentRoute.JsonMediaType));
        Produces<CollectionBody>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var database = await DocumentRoute.TenantAsync(HttpContext, tenants, cancellationToken);
        DocumentRoute.Parameters(HttpContext);
        DocumentRoute.RejectIdempotencyKey(HttpContext);
        var body = await DocumentRoute.ReadBodyAsync(HttpContext, DocumentRoute.MaxRequestBytes, "invalid_request", cancellationToken, DocumentRoute.JsonMediaType);
        var name = DocumentRoute.CollectionName(RequestJson.Deserialize<CreateCollectionRequest>(body, "invalid_request").Name);
        var collection = await documents.CreateCollectionAsync(database, name, cancellationToken);
        return TypedResults.Created(
            $"/v1/projects/{Uri.EscapeDataString(DocumentRoute.Project(HttpContext))}/databases/{DocumentRoute.Database(HttpContext)}/collections/{name}", Body(collection));
    }

    internal static CollectionBody Body(CollectionInfo collection) => new(collection.Name, DocumentRoute.Decimal(collection.CreatedAt));
}
