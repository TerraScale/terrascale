using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Home
{
    public sealed class HomeRegionsEndpoint(DashboardSessions sessions, AccountService accounts, IHomeMeasurements measurements) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/overview/regions");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(HomeEndpoints.RegionsAsync(HttpContext, sessions, accounts, measurements));
    }
}
