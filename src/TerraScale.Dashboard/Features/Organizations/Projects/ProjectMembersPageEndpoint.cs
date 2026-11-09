using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations.Projects
{
    public sealed class ProjectMembersPageEndpoint(DashboardSessions sessions, AccountService accounts, ProjectGrantService grants) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/project/members");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(ProjectGrantEndpoints.PageAsync(HttpContext, sessions, accounts, grants));
    }
}
