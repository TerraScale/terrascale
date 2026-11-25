using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

internal sealed class GetDatabaseAnalyticsEndpoint(DatabaseAccess access, UsageReports reports) : Endpoint<UsageInput, AnalyticsResponse>
{
    public override void Configure()
    {
        Get(ManagementRoute.Database + "/analytics");
        Name("getDatabaseAnalytics");
        Summary("A database's monthly usage history");
        Tags("Usage");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<AnalyticsResponse> HandleAsync(UsageInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await access.ManagedAsync(HttpContext.Caller, request.Project, request.Database, Scopes.DatabasesRead, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved => await reports.AnalyticsAsync(UsageSubject.Database(resolved.Project.Id, resolved.Database.Id), cancellationToken) switch
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
