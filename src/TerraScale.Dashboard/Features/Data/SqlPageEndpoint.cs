using TerraScale.Dashboard.Features.Databases;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Data
{
    public sealed class SqlPageEndpoint(DashboardSessions sessions, AccountService accounts, DataService data, SavedQueryService saved) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/databases/{databaseId}/sql").AuthenticateInHandler();

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            if (http.Request.RouteValues["databaseId"] is not string databaseId)
            {
                return TypedResults.NotFound();
            }

            var required = await AccountRequest.RequireAsync(http, sessions, accounts);
            return required switch
            {
                AccountContext context => await DataEndpoints.PageAsync(http, context, databaseId, data, saved),
                AccountUnauthenticated => AccountRequest.LoginRedirect(http),
                SessionUnavailable => AccountRequest.ServiceUnavailable(http),
            };
        }
    }
}
