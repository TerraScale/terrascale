using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Rules
{
    public sealed class RulesPageEndpoint(DashboardSessions sessions, AccountService accounts, RulesBackend rules) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/databases/{databaseId}/rules");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(RulesEndpoints.PageAsync(HttpContext, sessions, accounts, rules));
    }
}
