using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Auth
{
    public sealed class AuthStepUpCallbackEndpoint(AuthFlows flows, DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/auth/step-up/callback");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(StepUpFlow.CallbackAsync(HttpContext, flows, sessions, accounts));
    }
}
