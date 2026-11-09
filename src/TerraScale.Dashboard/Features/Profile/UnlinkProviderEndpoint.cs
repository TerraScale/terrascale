using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class UnlinkProviderEndpoint(DashboardSessions sessions, AccountService accounts, ITsAuthProviders providers) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/profile/providers/{provider}/unlink");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(SignInMethods.UnlinkAsync(HttpContext, HttpContext.Request.RouteValues["provider"]?.ToString() ?? "", sessions, accounts, providers));
    }
}
