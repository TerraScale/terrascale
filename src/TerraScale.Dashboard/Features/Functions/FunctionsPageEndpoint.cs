using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Functions
{
    public sealed class FunctionsPageEndpoint(DashboardSessions sessions, AccountService accounts, FunctionBackend functions) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/databases/{databaseId}/functions");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(FunctionEndpoints.PageAsync(HttpContext, sessions, accounts, functions));
    }
}
