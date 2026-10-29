using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Organizations.Invitations
{
    public sealed record InvitationPage(DocumentModel Document, string CsrfToken, string Token, PendingInvitation? Pending, SessionRecord? Session, string Error = "")
    {
        public bool CanRespond => Pending is not null && Session is not null && Pending.Invitation.Email.Equals(Session.Email, StringComparison.OrdinalIgnoreCase);
        public string Path => "/invitations/" + Uri.EscapeDataString(Token);
        public string Login => "/login?returnTo=" + Uri.EscapeDataString(Path);
    }

    public static class InvitationEndpoints
    {
        public static IEndpointRouteBuilder MapInvitations(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/invitations/{token}", async (HttpContext http, DashboardSessions sessions, InvitationService invitations, string token) =>
                await PageAsync(http, sessions, invitations, token));
            endpoints.MapPost("/invitations/{token}/respond", async (HttpContext http, DashboardSessions sessions, InvitationService invitations, string token) =>
            {
                Protect(http);
                var form = await http.Request.ReadFormAsync(http.RequestAborted);
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
                    // Invitation destinations come from the verified offer, never caller-supplied workspace query parameters.
                    http.Response.Headers.Location = action == "decline" ? "/databases?invitation=declined" : answered.Project is { } project
                        ? "/databases?organizationId=" + answered.Organization.Id + "&projectId=" + project.Id
                        : "/organization?tab=members&invitation=accepted";
                    return Results.StatusCode(StatusCodes.Status303SeeOther);
                }
                catch (TerraScale.Dashboard.Common.Accounts.AccountAccessException error)
                { return await PageAsync(http, sessions, invitations, token, error.Message, error.StatusCode); }
            });
            // ts-auth answers prompt=login with login_required, so switching accounts signs out of ts-auth too;
            // the invitee then signs in with the invited email and opens the link again.
            endpoints.MapPost("/invitations/{token}/switch-account", async (HttpContext http, string token) =>
            {
                Protect(http);
                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form))
                {
                    return Results.StatusCode(403);
                }

                return await Features.Auth.AuthEndpoints.SignOutAsync(http);
            });
            return endpoints;
        }

        private static void Protect(HttpContext http)
        {
            http.Response.Headers.CacheControl = "no-store";
            // Retain the origin on native form POSTs without exposing the token path.
            http.Response.Headers["Referrer-Policy"] = "strict-origin";
        }

        private static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, InvitationService invitations, string token, string error = "", int status = 200)
        {
            Protect(http);
            // ts-auth looks an invitation up only for a signed-in holder of its token.
            var session = await sessions.ReadAsync(http);
            var pending = await invitations.FindAsync(session, token, http.RequestAborted);
            var slice = InvitationView.Create(new(new(pending?.Project is null ? "Invitation" : "Project invitation", DocumentModel.NonceFor(http)), Csrf.Token(http), token,
                pending, session, error));
            slice.StatusCode = pending is null && session is not null ? 404 : status;
            return slice;
        }
    }
}
