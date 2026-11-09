using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Functions
{
    public sealed class DeleteFunctionEndpoint(DashboardSessions sessions, AccountService accounts, FunctionBackend functions) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/functions/delete");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(FunctionEndpoints.DeleteAsync(HttpContext, sessions, accounts, functions));
    }
}
