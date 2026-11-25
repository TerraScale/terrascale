using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

internal sealed class GetProjectUsageEndpoint(DatabaseAccess access, UsageReports reports) : Endpoint<UsageInput, UsageResponse>
{
    public override void Configure()
    {
        Get(ManagementRoute.ProjectApi + "/usage");
        Name("getProjectUsage");
        Summary("A project's usage in a month (default: this month)");
        Tags("Usage");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<UsageResponse> HandleAsync(UsageInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await access.ProjectAsync(HttpContext.Caller, request.Project, Scopes.DatabasesRead, cancellationToken);
        return outcome switch
        {
            ResolvedProject resolved => await reports.UsageAsync(UsageSubject.Project(resolved.Project.Id), request.Month, cancellationToken) switch
            {
                UsageBody usage => usage,
                InvalidInput invalid => invalid,
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
