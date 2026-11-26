using Microsoft.AspNetCore.Http.HttpResults;
using System.Diagnostics;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations.Invitations
{

    public sealed class InvitationRespondEndpoint(DashboardSessions sessions, InvitationService invitations) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/invitations/{token}/respond").AuthenticateInHandler();

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var token = http.Request.RouteValues["token"]?.ToString() ?? "";
            InvitationEndpoints.Protect(http);
            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/invitations/" + Uri.EscapeDataString(token), TypedResults.StatusCode(403));
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
                return answered switch
                {
                    DashboardValidation error => await InvitationEndpoints.PageAsync(http, sessions, invitations, token, error.Message, 400),
                    DashboardNotFound error => await InvitationEndpoints.PageAsync(http, sessions, invitations, token, error.Message, 404),
                    DashboardForbidden error => await InvitationEndpoints.PageAsync(http, sessions, invitations, token, error.Message, 403),
                    PendingInvitation pending => Accepted(http, pending, action),
                };
            }
            catch (TerraScale.Dashboard.Common.Accounts.AccountAccessException error)
            { return await InvitationEndpoints.PageAsync(http, sessions, invitations, token, error.Message, error.StatusCode); }
        }

        private static StatusCodeHttpResult Accepted(HttpContext http, PendingInvitation pending, string action)
        {
            http.Response.Headers.Location = action == "decline" ? "/databases?invitation=declined" : pending.Project is { } project
                ? "/databases?organizationId=" + pending.Organization.Id + "&projectId=" + project.Id
                : "/organization?tab=members&invitation=accepted";
            return TypedResults.StatusCode(StatusCodes.Status303SeeOther);
        }
    }
}
