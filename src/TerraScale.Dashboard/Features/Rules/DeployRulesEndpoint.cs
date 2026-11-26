using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Rules
{
    public sealed class DeployRulesEndpoint(DashboardSessions sessions, AccountService accounts, RulesBackend rules) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/rules/deploy").AuthenticateInHandler();

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            RequestBodies.Allow(HttpContext, RequestBodies.SourceForm);
            return new(RulesEndpoints.DeployAsync(HttpContext, sessions, accounts, rules));
        }
    }
}
