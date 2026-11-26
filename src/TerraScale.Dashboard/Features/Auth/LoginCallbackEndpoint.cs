using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Auth
{
    public sealed class LoginCallbackEndpoint(AuthFlows flows, IAuthOidcGateway auth, DashboardSessions sessions, EntryRateLimits limits) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/login/callback").AuthenticateInHandler();

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            limits.AdmitSignIn(HttpContext) ? new(AuthEndpoints.CompleteAsync(HttpContext, flows, auth, sessions)) : new(EntryRateLimits.Refuse(HttpContext));
    }
}
