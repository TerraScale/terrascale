using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed class DashboardContextEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/api/v1/dashboard/context");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var context = await AccountRequest.RequireAsync(HttpContext, sessions, accounts);
            return context is null ? Results.Unauthorized() : Results.Json(AccountContextView.From(context));
        }
    }
}
