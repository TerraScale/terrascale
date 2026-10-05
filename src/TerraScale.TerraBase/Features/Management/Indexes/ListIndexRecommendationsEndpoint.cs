using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Indexes;

/// <summary><c>GET …/databases/{database}/index-recommendations</c>: recommends indexes from the SQL workload.</summary>
public sealed class ListIndexRecommendationsEndpoint(IManagementService management, IIndexService indexes) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.IndexRecommendations);
        Name("listIndexRecommendations");
        Summary("Recommends indexes from the SQL workload");
        Description("One single-field recommendation per collection and field that runSql statements filtered or sorted on while no index of the collection led with it. Most queried first, then by id (collection:field)." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<IndexRecommendationListBody>();
        Produces<string>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is { } database
            ? TypedResults.Ok(await indexes.RecommendAsync(database, cancellationToken))
            : TypedResults.NotFound();
}
