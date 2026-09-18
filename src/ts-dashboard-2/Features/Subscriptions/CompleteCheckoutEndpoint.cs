using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Subscriptions;

public sealed class CompleteCheckoutEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/checkout/{sessionId}/complete");
        Summary("Confirms the local test payment method");
        Tags("Subscriptions");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(Results.Redirect("/onboarding?payment=success", false, false));
}
