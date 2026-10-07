using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

/// <summary><c>GET …/projects/{project}/usage</c>: sums the usage of every database of a project.</summary>
public sealed class GetProjectUsageEndpoint(IUsageService usage, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    /// <summary>The bounds of the project usage route.</summary>
    internal const string LimitsDescription =
        "Every database is listed before any is counted: more than 100 answer 400 \"query too expensive\" without a count; so do requests still running after 5 seconds, which start no further catalog read or count.";

    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Project + "/usage");
        Name("getProjectUsage");
        Summary("Reads the usage of a project");
        Description("Sums the usage of every database of the project and lists each one, in creation order, with its period but no series. " + LimitsDescription + " " + GetDatabaseUsageEndpoint.CountsDescription + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ProjectUsage>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        UsageWindow.Parse(HttpContext.Request.Query, time.GetUtcNow()) switch
        {
            Validation error => ErrorResponse.From(error),
            UsageWindow window => await ReadAsync(window, cancellationToken),
        };

    private async ValueTask<IResult> ReadAsync(UsageWindow window, CancellationToken cancellationToken)
    {
        try
        {
            return await usage.GetProjectUsageAsync(
                ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), window, cancellationToken) is { } project
                ? TypedResults.Ok(project)
                : TypedResults.NotFound();
        }
        catch (QueryCapacityException)
        {
            return TypedResults.BadRequest("query too expensive");
        }
    }
}
