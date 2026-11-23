using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

internal sealed class GetProjectMetricsEndpoint(DatabaseAccess access, UsageReports reports) : Endpoint<UsageInput, AnalyticsResponse>
{
    public override void Configure()
    {
        Get(ManagementRoute.ProjectApi + "/metrics");
        Name("getProjectMetrics");
        Summary("A project's monthly usage history");
        Tags("Usage");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<AnalyticsResponse> HandleAsync(UsageInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await access.ProjectAsync(HttpContext.Caller, request.Project, Scopes.DatabasesRead, cancellationToken);
        return outcome switch
        {
            ResolvedProject resolved => await reports.AnalyticsAsync(UsageMeter.ProjectScope(resolved.Project.Id), cancellationToken) switch
            {
                AnalyticsBody analytics => analytics,
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
