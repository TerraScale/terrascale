using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.ApiKeys
{
    public sealed class ApiKeyDetailsPageEndpoint(DashboardSessions sessions, AccountService accounts, ApiKeyService keys, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/api-keys/{keyId}").AuthenticateInHandler();

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            if (http.Request.RouteValues["keyId"] is not string keyId)
            {
                return TypedResults.NotFound();
            }

            var required = await AccountRequest.RequireAsync(http, sessions, accounts);
            return required switch
            {
                AccountContext context => await ApiKeyPages.DetailsAsync(http, context, keys, databases, keyId),
                AccountUnauthenticated => AccountRequest.LoginRedirect(http),
                SessionUnavailable => AccountRequest.ServiceUnavailable(http),
            };
        }
    }
}
