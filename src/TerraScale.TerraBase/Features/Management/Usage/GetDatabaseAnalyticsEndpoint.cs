using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

/// <summary>Project-token scoped analytics for a database.</summary>
public sealed class GetDatabaseAnalyticsEndpoint(IUsageAnalytics analytics, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(AnalyticsRoute.Database + "/analytics");
        Name("getDatabaseAnalytics");
        Summary("Reads database analytics");
        Description("Client requests by HTTP method, committed document operations by type, errors, mergeable histogram latency percentiles, sampled on-disk storage and separate client and replication body/message bandwidth. " + AnalyticsWindow.Description + " " + ProjectApiRoute.IdentityDescription);
        Tags("Analytics");
        Produces<ProjectAnalytics>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        AnalyticsRoute.ReadAsync(HttpContext, analytics, time, replication: false, cancellationToken);
}
