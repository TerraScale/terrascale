using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class SendProviderCodeEndpoint(DashboardSessions sessions, AccountService accounts, ITsAuthProviders providers) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/profile/providers/{provider}/code");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(SignInMethods.SendCodeAsync(HttpContext, HttpContext.Request.RouteValues["provider"]?.ToString() ?? "", sessions, accounts, providers));
    }
}
