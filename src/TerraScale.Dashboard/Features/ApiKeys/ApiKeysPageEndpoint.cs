using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.ApiKeys
{
    public sealed class ApiKeysPageEndpoint(DashboardSessions sessions, AccountService accounts, ApiKeyService keys, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/api-keys").AuthenticateInHandler();

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var required = await AccountRequest.RequireAsync(http, sessions, accounts);
            return required switch
            {
                AccountContext context => await ApiKeyPages.PageAsync(http, context, keys, databases),
                AccountUnauthenticated => AccountRequest.LoginRedirect(http),
                SessionUnavailable => AccountRequest.ServiceUnavailable(http),
            };
        }
    }
}
