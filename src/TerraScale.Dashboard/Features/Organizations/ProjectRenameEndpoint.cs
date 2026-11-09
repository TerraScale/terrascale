using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed class ProjectRenameEndpoint(DashboardSessions sessions, AccountService accounts, OrganizationOperations operations) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/project/rename");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(ProjectSettingsEndpoints.RenameAsync(HttpContext, sessions, accounts, operations));
    }
}
