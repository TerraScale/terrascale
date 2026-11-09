using TerraScale.Dashboard.Platform.Contracts;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Accounts
{
    public sealed class SwitchProjectEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Post("/api/v1/dashboard/switch-project");
            Metadata(new IntegrationEndpointGroup("integration"));
            Name("switchDashboardProject");
            Tags("Workspace");
            Summary("Select a project the current user can access");
            Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(SwitchProjectRequest), false));
            Metadata(new ProducesResponseTypeMetadata(StatusCodes.Status303SeeOther, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(StatusCodes.Status400BadRequest, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(StatusCodes.Status401Unauthorized, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(StatusCodes.Status403Forbidden, typeof(ProblemDetails), ["application/problem+json"]));
        }

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            await AccountEndpoints.SwitchAsync(HttpContext, sessions, accounts, "projectId",
                (session, id, ct) => accounts.SwitchProjectAsync(session, id, ct));
    }
}
