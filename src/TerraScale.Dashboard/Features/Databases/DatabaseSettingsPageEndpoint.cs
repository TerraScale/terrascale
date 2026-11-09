using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed class DatabaseSettingsPageEndpoint(DashboardSessions sessions, AccountService accounts, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/databases/{databaseId}/settings");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(DatabaseSettingsEndpoints.PageAsync(HttpContext, sessions, accounts, databases, HttpContext.Request.RouteValues["databaseId"]?.ToString() ?? ""));
    }
}
