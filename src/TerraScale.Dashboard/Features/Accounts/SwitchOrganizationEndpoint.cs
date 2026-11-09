using TerraScale.Dashboard.Platform.Contracts;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Accounts
{
    public sealed class SwitchOrganizationEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Post("/api/v1/dashboard/switch-organization");
            Metadata(new IntegrationEndpointGroup("integration"));
            Name("switchDashboardOrganization");
            Tags("Workspace");
            Summary("Select an organization the current user can access");
            Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(SwitchOrganizationRequest), false));
            Metadata(new ProducesResponseTypeMetadata(StatusCodes.Status303SeeOther, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(StatusCodes.Status400BadRequest, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(StatusCodes.Status401Unauthorized, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(StatusCodes.Status403Forbidden, typeof(ProblemDetails), ["application/problem+json"]));
        }

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            await AccountEndpoints.SwitchAsync(HttpContext, sessions, accounts, "organizationId",
                (session, id, ct) => accounts.SwitchOrganizationAsync(session, id, ct));
    }
}
