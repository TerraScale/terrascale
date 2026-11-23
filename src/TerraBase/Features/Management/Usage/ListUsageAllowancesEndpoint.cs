using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

internal sealed class ListUsageAllowancesEndpoint : EndpointWithoutRequest<AllowancesBody>
{
    public override void Configure()
    {
        Get("/v1/usage/allowances");
        Name("listUsageAllowances");
        Summary("Plan allowances");
        Tags("Usage");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override ValueTask<AllowancesBody> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult(UsageReports.Allowances());
}
