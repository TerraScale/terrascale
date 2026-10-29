using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Queries;

public sealed class ListDashboardQueryShapesEndpoint(IQueryShapeTracker tracker) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/management/query-shapes");
        Name("listDashboardQueryShapes");
        Summary("Lists observed dashboard query shapes and workload statistics");
        Description("At most 128 canonical shapes of the dashboard document space, with literal values removed, including pending local observations. " +
            "Most queried first, then by collection and shape id. Statistics are best-effort; new shapes are dropped when bounds are full. " +
            "Internal listener only, as the platform operator: a ts-auth user token gets 403. " +
            "Pending observations are lost on crash.");
        Tags(ManagementRoute.Tag);
        Produces<QueryShapeListBody>();
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (!HttpContext.User.IsOperator())
        {
            return ErrorResponse.From(new Forbidden("the dashboard document space needs the platform operator"));
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
