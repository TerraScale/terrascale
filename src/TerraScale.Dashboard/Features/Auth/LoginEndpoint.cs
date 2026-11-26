using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Auth
{
    public sealed class LoginEndpoint(DashboardSessions sessions, EntryRateLimits limits) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            AllowAnonymous();
            Verbs("GET", "HEAD");
            Route("/login");
        }

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            limits.AdmitSignIn(HttpContext) ? new(AuthEndpoints.StartAsync(HttpContext, sessions)) : new(EntryRateLimits.Refuse(HttpContext));
    }
}
