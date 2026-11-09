using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed class RenameProjectEndpoint(DashboardSessions sessions, AccountService accounts, IWorkspacePlacement placement) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/organization/projects/{projectId}/rename");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(OrganizationEndpoints.MutateAsync(HttpContext, sessions, accounts, placement, "rename-project", HttpContext.Request.RouteValues["projectId"]?.ToString() ?? ""));
    }
}
