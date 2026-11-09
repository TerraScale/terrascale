using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations.Invitations
{
    public sealed class InvitationPageEndpoint(DashboardSessions sessions, InvitationService invitations) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/invitations/{token}");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(InvitationEndpoints.PageAsync(HttpContext, sessions, invitations, HttpContext.Request.RouteValues["token"]?.ToString() ?? ""));
    }
}
