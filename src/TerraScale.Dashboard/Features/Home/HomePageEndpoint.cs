using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Home
{
    public sealed class HomePageEndpoint(DashboardSessions sessions, AccountService accounts, IHomeMeasurements measurements) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/").AuthenticateInHandler();

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(HomeEndpoints.PageAsync(HttpContext, sessions, accounts, measurements));
    }
}
