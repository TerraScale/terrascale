using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Queries;

/// <summary><c>GET …/databases/{database}/query-shapes</c>: observed canonical query statistics.</summary>
internal sealed class ListQueryShapesEndpoint(IManagementService management, IQueryShapeTracker tracker) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.Database + "/query-shapes");
        Name("listQueryShapes");
        Summary("Lists observed query shapes and workload statistics");
        Description("At most 128 canonical shapes per database, with literal values removed, including pending observations on the primary. " +
            "Most queried first, then by collection and shape id. Each item has power-of-two millisecond latencyBuckets and their p95LatencyMs. Statistics are best-effort; new shapes are dropped when bounds are full. " +
            "Catalog followers forward observations to the primary and return replicated persisted statistics only; replication can delay visibility. Pending observations are lost on crash. " +
            ProjectApiRoute.IdentityDescription);
        Tags("Queries");
        Produces<QueryShapeListBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            TenantContext database => await tracker.ReadAsync(database, cancellationToken) switch
            {
                QueryShapeRead read => Retain(read),
                Unavailable error => ErrorResponse.From(error),
            },
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };

    private QueryShapeRead Retain(QueryShapeRead read)
    {
        HttpContext.Response.RegisterForDispose(read);
        return read;
    }

}
