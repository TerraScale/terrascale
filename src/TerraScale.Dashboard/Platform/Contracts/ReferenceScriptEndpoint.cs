using Microsoft.AspNetCore.Routing;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Platform.Contracts
{
    public sealed class ReferenceScriptEndpoint : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Get("/reference/terrascale.js");
            Metadata(new ExcludeFromDescriptionAttribute());
        }

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(DashboardContracts.ReferenceScript(HttpContext));
    }
}
