using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

internal sealed class GetProjectAnalyticsEndpoint(DatabaseAccess access, UsageReports reports) : Endpoint<UsageInput, Result<AnalyticsBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Get(ManagementRoute.ProjectApi + "/analytics");
        Name("getProjectAnalytics");
        Summary("A project's monthly usage history");
        Tags("Usage");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<Result<AnalyticsBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(UsageInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await access.ProjectAsync(HttpContext.Caller, request.Project, Scopes.DatabasesRead, cancellationToken) switch
        {
            ResolvedProject resolved => await reports.AnalyticsAsync(UsageMeter.ProjectScope(resolved.Project.Id), cancellationToken) switch
            {
                AnalyticsBody analytics => analytics,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
