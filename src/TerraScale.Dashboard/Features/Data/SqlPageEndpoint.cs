using TerraScale.Dashboard.Features.Databases;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Data
{
    public sealed class SqlPageEndpoint(DashboardSessions sessions, AccountService accounts, DataService data, SavedQueryService saved) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/databases/{databaseId}/sql");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            return context is null ? AccountRequest.LoginRedirect(http)
                : await DataEndpoints.PageAsync(http, context, http.Request.RouteValues["databaseId"]?.ToString() ?? "", data, saved);
        }
    }
}
