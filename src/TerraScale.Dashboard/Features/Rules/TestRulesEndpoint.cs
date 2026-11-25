using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Rules
{
    public sealed class TestRulesEndpoint(DashboardSessions sessions, AccountService accounts, RulesBackend rules) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/rules/test");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            RequestBodies.Allow(HttpContext, RequestBodies.SourceForm);
            return new(RulesEndpoints.TestAsync(HttpContext, sessions, accounts, rules));
        }
    }
}
