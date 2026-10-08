using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Queries;

/// <summary><c>GET /management/query-shapes</c>: workload statistics of the dashboard document space.</summary>
public sealed class ListDashboardQueryShapesEndpoint(IQueryShapeTracker tracker) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/management/query-shapes");
        Name("listDashboardQueryShapes");
        Summary("Lists observed dashboard query shapes and workload statistics");
        Description("At most 128 canonical shapes of the dashboard document space, with literal values removed, including pending observations. " +
            "Most queried first, then by collection and shape id. Statistics are best-effort; new shapes are dropped when bounds are full. " +
            "Management service credential only (loopback administrative listener): a ts-auth user token gets 403. " +
            "Catalog followers add their local pending observations to replicated statistics; local observations are not forwarded or persisted and are lost on restart.");
        Tags(ManagementRoute.Tag);
        Produces<QueryShapeListBody>();
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (!HttpContext.User.IsManagementService())
        {
            return ErrorResponse.From(new Forbidden("the dashboard document space needs the management service credential"));
        }

        return await tracker.ReadAsync(null, cancellationToken) switch
        {
            QueryShapeRead read => Retain(read),
            Unavailable error => ErrorResponse.From(error),
        };
    }

    private QueryShapeRead Retain(QueryShapeRead read)
    {
        HttpContext.Response.RegisterForDispose(read);
        return read;
    }

}
