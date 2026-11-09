using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed class ProjectSettingsPageEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/project");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(ProjectSettingsEndpoints.PageAsync(HttpContext, sessions, accounts));
    }
}
