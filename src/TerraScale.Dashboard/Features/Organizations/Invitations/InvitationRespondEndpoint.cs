using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations.Invitations
{
    public sealed class InvitationRespondEndpoint(DashboardSessions sessions, InvitationService invitations) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/invitations/{token}/respond");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var token = http.Request.RouteValues["token"]?.ToString() ?? "";
            InvitationEndpoints.Protect(http);
            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return Results.StatusCode(403);
            }

            var session = await sessions.ReadAsync(http);
            if (session is null)
            {
                return HttpRedirects.SeeOther("/login?returnTo=" + Uri.EscapeDataString("/invitations/" + Uri.EscapeDataString(token)));
            }

            try
            {
                var action = form["action"].ToString().Trim().ToLowerInvariant();
                var answered = await invitations.RespondAsync(session, token, action, http.RequestAborted);
                http.Response.Headers.Location = action == "decline" ? "/databases?invitation=declined" : answered.Project is { } project
                    ? "/databases?organizationId=" + answered.Organization.Id + "&projectId=" + project.Id
                    : "/organization?tab=members&invitation=accepted";
                return Results.StatusCode(StatusCodes.Status303SeeOther);
            }
            catch (TerraScale.Dashboard.Common.Accounts.AccountAccessException error)
            { return await InvitationEndpoints.PageAsync(http, sessions, invitations, token, error.Message, error.StatusCode); }
        }
    }
}
