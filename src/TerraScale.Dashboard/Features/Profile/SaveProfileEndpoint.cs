using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class SaveProfileEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/profile");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(ProfileEndpoints.SaveAsync(HttpContext, sessions, accounts, false));
    }
}
