using TerraScale.Endpoints;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Errors;
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
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DocumentRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            TenantContext database => (DocumentRoute.CheckParameters(HttpContext), DocumentRoute.CheckIdempotencyKey(HttpContext)) switch
            {
                (Validation error, _) => ErrorResponse.From(error),
                (_, Validation error) => ErrorResponse.From(error),
                _ => await DocumentRoute.CheckBodyAsync(HttpContext, DocumentRoute.MaxRequestBytes, "invalid_request", cancellationToken, DocumentRoute.JsonMediaType) switch
                {
                    Validation error => ErrorResponse.From(error),
                    PayloadTooLarge error => ErrorResponse.From(error),
                    UnsupportedMediaType error => ErrorResponse.From(error),
                    byte[] body => RequestJson.Read<CreateCollectionRequest>(body, "invalid_request") switch
                    {
                        Validation error => ErrorResponse.From(error),
                        CreateCollectionRequest request => DocumentRoute.CheckCollectionName(request.Name) switch
                        {
                            Validation error => ErrorResponse.From(error),
                            string name => await CreateAsync(database, name, cancellationToken),
                        },
                    },
                },
            },
        };

    private async ValueTask<IResult> CreateAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        var collection = await documents.CreateCollectionAsync(database, name, cancellationToken);
        return TypedResults.Created(
            $"/v1/projects/{Uri.EscapeDataString(DocumentRoute.Project(HttpContext))}/databases/{DocumentRoute.Database(HttpContext)}/collections/{name}", Body(collection));
    }

    internal static CollectionBody Body(CollectionInfo collection) => new(collection.Name, DocumentRoute.Decimal(collection.CreatedAt));
}
