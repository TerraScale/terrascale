using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed class DashboardContextEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/api/v1/dashboard/context").AuthenticateInHandler();

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var required = await AccountRequest.RequireAsync(HttpContext, sessions, accounts);
            return required switch
            {
                AccountContext context => TypedResults.Json(AccountContextView.From(context)),
                AccountUnauthenticated => TypedResults.Unauthorized(),
                SessionUnavailable => AccountRequest.ServiceUnavailable(HttpContext),
            };
        }
    }
}
