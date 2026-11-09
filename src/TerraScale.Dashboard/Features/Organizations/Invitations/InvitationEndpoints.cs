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
        internal static void Protect(HttpContext http)
        {
            http.Response.Headers.CacheControl = "no-store";
            // Retain the origin on native form POSTs without exposing the token path.
            http.Response.Headers["Referrer-Policy"] = "strict-origin";
        }

        internal static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, InvitationService invitations, string token, string error = "", int status = 200)
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
