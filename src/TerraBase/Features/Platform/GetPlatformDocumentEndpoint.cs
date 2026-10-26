using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Platform;

/// <summary><c>GET /v1/platform/databases/{database}/collections/{collection}/documents/by-key?id=</c>: one redacted document, for platform admins.</summary>
public sealed class GetPlatformDocumentEndpoint(IDocumentService documents, ITenantDirectory tenants, ILogger<GetPlatformDocumentEndpoint> logger)
    : EndpointWithoutRequest<IResult>
{
    private static readonly NotFound Missing = new("the document does not exist", "not_found");

    /// <inheritdoc />
    public override void Configure()
    {
        Get(PlatformExplore.Documents + "/by-key");
        Name("getPlatformDocument");
        Summary("Reads a platform document, redacted");
        Description("Platform admins only (platform_role admin). Query: id, the document's ULID. Secret-looking fields read \"[redacted]\".");
        Tags("Platform");
        Produces<DocumentBody>();
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(PlatformExplore.PolicyName);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var name = HttpContext.Request.RouteValues["database"] as string;
        var found = await PlatformExplore.FindAsync(tenants, name, cancellationToken);
        if (found is not TenantContext database)
        {
            return PlatformExplore.Refused(found);
        }

        if ((DocumentRoute.CheckCollectionName(HttpContext), DocumentRoute.CheckDocumentParameter(HttpContext)) is not (string collection, string id))
        {
            return ErrorResponse.From(new Validation("collection and id are required"));
        }

        PlatformExplore.Audit(logger, HttpContext, name!, collection, id);
        return await documents.GetAsync(new DocumentKey(database, collection, id), cancellationToken) switch
        {
            StoredDocument { Deleted: false } document => Accepted(database, collection, document, cancellationToken),
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            _ => ErrorResponse.From(Missing),
        };
    }

    private IResult Accepted(TenantContext database, string collection, StoredDocument document, CancellationToken cancellationToken)
    {
        var body = Redacted(collection, document, cancellationToken);
        documents.AcceptRead(new DocumentKey(database, collection, document.Id), document);
        return TypedResults.Ok(body);
    }

    /// <summary>The document's body with every secret-looking field redacted.</summary>
    internal static DocumentBody Redacted(string collection, StoredDocument document, CancellationToken cancellationToken)
    {
        var body = GetDocumentEndpoint.Body(collection, document, cancellationToken: cancellationToken);
        return body with { Document = PlatformExplore.Redact(body.Document) };
    }
}
