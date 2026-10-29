using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

public sealed class GetProjectAnalyticsEndpoint(IUsageAnalytics analytics, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(AnalyticsRoute.Project + "/analytics");
        Name("getProjectAnalytics");
        Summary("Reads project analytics");
        Description("Client requests by HTTP method, committed document operations by type, errors, mergeable histogram latency percentiles, sampled on-disk storage and separate client and replication body/message bandwidth. " + AnalyticsWindow.Description + " " + ProjectApiRoute.IdentityDescription);
        Tags("Analytics");
        Produces<ProjectAnalytics>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
    AnalyticsRoute.ReadAsync(HttpContext, analytics, time, cancellationToken);
}
