using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Platform;

/// <summary><c>GET /v1/platform/databases/{database}/collections</c>: a platform database's collections, for platform admins.</summary>
public sealed class ListPlatformCollectionsEndpoint(IDocumentService documents, ITenantDirectory tenants, ILogger<ListPlatformCollectionsEndpoint> logger)
    : EndpointWithoutRequest<IResult>
{
    // A platform database has a few dozen collections; one page holds them all.
    private const int MaxCollections = 200;

    /// <inheritdoc />
    public override void Configure()
    {
        Get(PlatformExplore.Collections);
        Name("listPlatformCollections");
        Summary("Lists a platform database's collections");
        Description("Platform admins only (platform_role admin). database: platform-auth or platform-dashboard; 404 for any other. Up to 200 collections in name order.");
        Tags("Platform");
        Produces<CollectionPageBody>();
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

        PlatformExplore.Audit(logger, HttpContext, name!, null, null);
        return await documents.ListCollectionsAsync(database, afterName: null, MaxCollections, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            CollectionPage page => TypedResults.Ok(new CollectionPageBody([.. page.Collections.Select(CreateCollectionEndpoint.Body)], null)),
        };
    }
}
