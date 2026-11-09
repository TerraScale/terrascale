using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed class BillingRedirectEndpoint : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/billing");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Results.Redirect("/organization?tab=billing"));
    }
}
