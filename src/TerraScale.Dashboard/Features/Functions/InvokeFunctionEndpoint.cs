using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Functions
{
    public sealed class InvokeFunctionEndpoint(DashboardSessions sessions, AccountService accounts, FunctionBackend functions) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/functions/invoke");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(FunctionEndpoints.InvokeAsync(HttpContext, sessions, accounts, functions));
    }
}
