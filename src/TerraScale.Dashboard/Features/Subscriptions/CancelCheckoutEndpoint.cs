using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Subscriptions;

public sealed class CancelCheckoutEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/checkout/{sessionId}/cancel");
        Summary("Cancels the local checkout session");
        Tags("Subscriptions");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(Results.Redirect("/onboarding?payment=canceled", false, false));
}
