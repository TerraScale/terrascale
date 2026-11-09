using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class SignOutEverywhereEndpoint(DashboardSessions sessions, AccountService accounts, ITsAuthPlatform platform) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/profile/sign-out-everywhere");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(ProfileEndpoints.SignOutEverywhereAsync(HttpContext, sessions, accounts, platform));
    }
}
