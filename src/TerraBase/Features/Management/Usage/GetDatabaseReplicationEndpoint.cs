using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

/// <summary>Project-token scoped replication for a database.</summary>
public sealed class GetDatabaseReplicationEndpoint(IUsageAnalytics analytics, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(AnalyticsRoute.Database + "/replication");
        Name("getDatabaseReplication");
        Summary("Reads database replication");
        Description("Replication operation counts, encoded wire message bytes by directed region pair and kind, backlog age, journal divergence, last applied and convergence time. Unknown or stale observations are stopped with null lag and divergence. " + AnalyticsWindow.Description + " " + ProjectApiRoute.IdentityDescription);
        Tags("Replication");
        Produces<ProjectReplication>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        AnalyticsRoute.ReadAsync(HttpContext, analytics, time, replication: true, cancellationToken);
}
