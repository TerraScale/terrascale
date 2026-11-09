using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Auth
{
    public sealed class LoginEndpoint(DashboardSessions sessions) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Verbs("GET", "HEAD");
            Route("/login");
        }

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AuthEndpoints.StartAsync(HttpContext, sessions));
    }
}
