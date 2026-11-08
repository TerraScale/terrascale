using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

/// <summary><c>GET …/projects/{project}/usage</c>: sums the usage of every database of a project.</summary>
internal sealed class GetProjectUsageEndpoint(IUsageService usage, PeerUsage peers, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    /// <summary>The bounds of the project usage route.</summary>
    internal const string LimitsDescription =
        "Every database is listed before any is counted: more than 100 answer 400 \"query too expensive\" without a count. A request still running after 5 seconds starts no further catalog read or count and answers 503 capacity with Retry-After; retry it unchanged.";

    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.Project + "/usage");
        Name("getProjectUsage");
        Summary("Reads the usage of a project");
        Description("Sums the usage of every database of the project and lists each one, in creation order, with its period but no series. " + LimitsDescription + " " + GetDatabaseUsageEndpoint.CountsDescription + " " + ProjectApiRoute.IdentityDescription);
        Tags("Usage");
        Produces<ProjectUsage>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        UsageWindow.Parse(HttpContext.Request.Query, time.GetUtcNow()) switch
        {
            Validation error => ErrorResponse.From(error),
            UsageWindow window => await ReadAsync(window, cancellationToken),
        };

    // This node's share, plus every other region's when the client asked: each region meters only what it served.
    private async ValueTask<IResult> ReadAsync(UsageWindow window, CancellationToken cancellationToken)
    {
        var organization = ManagementRoute.OrganizationId(HttpContext);
        var nodeOnly = PeerUsage.NodeOnly(HttpContext);
        var others = nodeOnly ? Task.FromResult<List<ProjectUsage>?>([]) : peers.ProjectAsync(HttpContext, cancellationToken);
        var month = PeerUsage.MonthOf(time.GetUtcNow());
        var units = nodeOnly
            ? Task.FromResult<Dictionary<string, UsageTotals>?>(new(StringComparer.Ordinal) { [organization] = new UsageTotals(0, 0) })
            : peers.MonthsAsync([organization], month, cancellationToken);
        var local = await usage.GetProjectUsageAsync(
            ManagementRoute.UserId(HttpContext), organization, ManagementRoute.Value(HttpContext, "project"), window, cancellationToken);
        if (local is not ProjectUsage project)
        {
            return local switch
            {
                NotFound error => ErrorResponse.From(error),
                Validation error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
                ProjectUsage => throw new System.Diagnostics.UnreachableException(),
            };
        }

        if (await others is not { } remote || await units is not { } remoteUnits || project.Month.Month != month)
        {
            return ErrorResponse.From(new Unavailable("usage of another region is unavailable; retry"));
        }

        return TypedResults.Ok(PeerUsage.MergeProject(project, remote, remoteUnits[organization]));
    }
}
