using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Rules
{
    public sealed class RestoreRulesEndpoint(DashboardSessions sessions, AccountService accounts, RulesBackend rules) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/rules/restore");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(RulesEndpoints.RestoreAsync(HttpContext, sessions, accounts, rules));
    }
}
