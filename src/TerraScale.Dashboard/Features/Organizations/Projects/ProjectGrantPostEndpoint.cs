using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations.Projects
{
    public sealed class ProjectGrantPostEndpoint(DashboardSessions sessions, AccountService accounts, ProjectGrantService grants) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/project/grants");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(ProjectGrantEndpoints.MutateAsync(HttpContext, sessions, accounts, grants));
    }
}
