using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Rules
{
    public sealed class DeployRulesEndpoint(DashboardSessions sessions, AccountService accounts, RulesBackend rules) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/rules/deploy");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(RulesEndpoints.DeployAsync(HttpContext, sessions, accounts, rules));
    }
}
