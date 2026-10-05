using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Indexes;

/// <summary><c>POST …/index-recommendations/{recommendation}/apply</c>: creates the recommended index.</summary>
public sealed class ApplyIndexRecommendationEndpoint(IManagementService management, IIndexService indexes) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.IndexRecommendations + "/{recommendation}/apply");
        Name("applyIndexRecommendation");
        Summary("Creates the index a recommendation describes");
        Description("201 with status applied and the new single-field index (default name), or 200 with status already-applied and the index that already leads with the field." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<AppliedIndexRecommendation>(StatusCodes.Status201Created);
        Produces<AppliedIndexRecommendation>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var writable = await ManagementRoute.WritableDatabaseAsync(HttpContext, management, cancellationToken);
        if (writable.Value is not { } database)
        {
            return ManagementRoute.Error(writable.Error);
        }

        var result = await indexes.ApplyAsync(database, ManagementRoute.Value(HttpContext, "recommendation"), cancellationToken);
        var databasePath = ManagementRoute.DatabasePath(database);
        return result switch
        {
            { Value: { Status: "applied" } applied } => TypedResults.Created($"{databasePath}/indexes/{applied.Index.Name}", applied),
            { Value: { } existing } => TypedResults.Ok(existing),
            { Error: ManagementError.NameTaken } => TypedResults.Conflict("another index already has the default name"),
            _ => ManagementRoute.Error(result.Error),
        };
    }
}
