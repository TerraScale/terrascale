using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Data
{
    public sealed class SqlCatalogEndpoint(DashboardSessions sessions, AccountService accounts, DataService data, SavedQueryService saved) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/data/sql/catalog");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(DataEndpoints.MutateAsync(HttpContext, sessions, accounts, data, saved, "sql/catalog"));
    }
}
