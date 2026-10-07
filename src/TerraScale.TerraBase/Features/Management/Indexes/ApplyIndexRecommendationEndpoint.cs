using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Indexes;

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
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.CheckWritableDatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            TenantContext database => await indexes.ApplyAsync(database, ManagementRoute.Value(HttpContext, "recommendation"), cancellationToken) switch
            {
                AppliedIndexRecommendation { Status: "applied" } applied =>
                    TypedResults.Created($"{ManagementRoute.DatabasePath(database)}/indexes/{applied.Index.Name}", applied),
                AppliedIndexRecommendation existing => TypedResults.Ok(existing),
                NotFound error => ErrorResponse.From(error),
                Conflict error => ErrorResponse.From(error),
            },
        };
}
