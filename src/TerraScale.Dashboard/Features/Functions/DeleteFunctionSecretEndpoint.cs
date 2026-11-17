using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Functions
{
    public sealed class DeleteFunctionSecretEndpoint(DashboardSessions sessions, AccountService accounts, FunctionBackend functions) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/functions/secrets/delete");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(FunctionEndpoints.SecretAsync(HttpContext, sessions, accounts, functions, delete: true));
    }
}
