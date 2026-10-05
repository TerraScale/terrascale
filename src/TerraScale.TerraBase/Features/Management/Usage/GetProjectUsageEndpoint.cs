using TerraScale.Endpoints;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Usage;

/// <summary><c>GET …/projects/{project}/usage</c>: sums the usage of every database of a project.</summary>
public sealed class GetProjectUsageEndpoint(IUsageService usage) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Project + "/usage");
        Name("getProjectUsage");
        Summary("Reads the usage of a project");
        Description("Sums the usage of every database of the project and lists each one, in creation order. A project of more than 100 databases answers 400 \"query too expensive\" before counting; so does one whose counts are still running after 5 seconds, which starts no further count. " + GetDatabaseUsageEndpoint.CountsDescription + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ProjectUsage>();
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await usage.GetProjectUsageAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), cancellationToken) is { } project
                ? TypedResults.Ok(project)
                : TypedResults.NotFound();
        }
        catch (QueryCapacityException)
        {
            return TypedResults.BadRequest("query too expensive");
        }
    }
}
