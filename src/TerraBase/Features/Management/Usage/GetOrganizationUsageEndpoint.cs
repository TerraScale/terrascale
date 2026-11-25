using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

internal sealed class GetOrganizationUsageEndpoint(UsageReports reports) : Endpoint<OrganizationUsageInput, OrganizationUsageResponse>
{
    public override void Configure()
    {
        Get("/v1/usage/organizations/{organization}");
        Name("getOrganizationUsage");
        Summary("An organization's usage per project and in total for a month (default: this month), with completeness");
        Tags("Usage");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<OrganizationUsageResponse> HandleAsync(OrganizationUsageInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await reports.OrganizationAsync(HttpContext.Caller, request, cancellationToken);
    }
}
