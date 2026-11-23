using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

internal sealed class GetProjectUsageEndpoint(DatabaseAccess access, UsageReports reports) : Endpoint<UsageInput, Result<UsageBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
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

    public override async ValueTask<Result<UsageBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(UsageInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await access.ProjectAsync(HttpContext.Caller, request.Project, Scopes.DatabasesRead, cancellationToken) switch
        {
            ResolvedProject resolved => await reports.UsageAsync(UsageMeter.ProjectScope(resolved.Project.Id), request.Month, cancellationToken) switch
            {
                UsageBody usage => usage,
                Validation invalid => invalid,
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
