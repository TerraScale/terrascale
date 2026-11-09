using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Onboarding
{
    public sealed class OnboardingStepEndpoint(DashboardSessions sessions, AccountService accounts, OnboardingService service, IDatabaseBackend backend) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/onboarding/step");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(OnboardingEndpoints.SaveAsync(HttpContext, sessions, accounts, service, backend));
    }
}
