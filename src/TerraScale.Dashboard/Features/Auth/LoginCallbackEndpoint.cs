using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Auth
{
    public sealed class LoginCallbackEndpoint(AuthFlows flows, IAuthOidcGateway auth, DashboardSessions sessions) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/login/callback");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AuthEndpoints.CompleteAsync(HttpContext, flows, auth, sessions));
    }
}
