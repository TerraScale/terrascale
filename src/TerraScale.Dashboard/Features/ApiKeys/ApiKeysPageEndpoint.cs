using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.ApiKeys
{
    public sealed class ApiKeysPageEndpoint(DashboardSessions sessions, AccountService accounts, ApiKeyService keys, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/api-keys");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            return context is null ? AccountRequest.LoginRedirect(http) : await ApiKeyPages.PageAsync(http, context, keys, databases);
        }
    }
}
