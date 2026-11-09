using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Onboarding
{
    public sealed class OnboardingPageEndpoint(DashboardSessions sessions, AccountService accounts, OnboardingService service, IDatabaseBackend backend) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/onboarding");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(OnboardingEndpoints.RenderAsync(HttpContext, sessions, accounts, service, backend));
    }
}
