using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

internal sealed class GetDatabaseUsageEndpoint(DatabaseAccess access, UsageReports reports) : Endpoint<UsageInput, UsageResponse>
{
    public override void Configure()
    {
        Get(ManagementRoute.Database + "/usage");
        Name("getDatabaseUsage");
        Summary("A database's usage in a month (default: this month)");
        Tags("Usage");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<UsageResponse> HandleAsync(UsageInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await access.ManagedAsync(HttpContext.Caller, request.Project, request.Database, Scopes.DatabasesRead, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved => await reports.UsageAsync(UsageMeter.DatabaseScope(resolved.Database.Id), request.Month, cancellationToken) switch
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
