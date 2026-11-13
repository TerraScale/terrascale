using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Functions
{
    public sealed class DeployFunctionEndpoint(DashboardSessions sessions, AccountService accounts, FunctionBackend functions) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/functions/deploy");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(FunctionEndpoints.DeployAsync(HttpContext, sessions, accounts, functions));
    }
}
