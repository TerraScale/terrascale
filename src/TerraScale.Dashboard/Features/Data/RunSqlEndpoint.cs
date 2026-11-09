using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Data
{
    public sealed class RunSqlEndpoint(DashboardSessions sessions, AccountService accounts, DataService data, SavedQueryService saved) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/data/sql");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(DataEndpoints.MutateAsync(HttpContext, sessions, accounts, data, saved, "sql"));
    }
}
