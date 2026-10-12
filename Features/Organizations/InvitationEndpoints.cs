using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.UI;

namespace TerraScale.Dashboard.Features.Organizations;

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
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
            var session = await sessions.ReadAsync(http);
            if (session is null) return AuthEndpoints.SeeOther("/login?returnTo=" + Uri.EscapeDataString("/invitations/" + Uri.EscapeDataString(token)));
            try
            {
                var action = form["action"].ToString().Trim().ToLowerInvariant();
                await invitations.RespondAsync(session, token, action, http.RequestAborted);
                return AuthEndpoints.SeeOther(action == "accept" ? "/organization?tab=members&invitation=accepted" : "/organization?invitation=declined");
            }
            catch (TerraScale.Dashboard.Features.Accounts.AccountAccessException error)
            { return await PageAsync(http, sessions, invitations, token, error.Message, error.StatusCode); }
        });
        endpoints.MapPost("/invitations/{token}/switch-account", async (HttpContext http, DashboardSessions sessions, string token) =>
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
            await sessions.EndAsync(http);
            return AuthEndpoints.SeeOther("/login?returnTo=" + Uri.EscapeDataString("/invitations/" + Uri.EscapeDataString(token)) + "&prompt=login");
        });
        return endpoints;
    }

    private static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, InvitationService invitations, string token, string error = "", int status = 200)
    {
        http.Response.Headers.CacheControl = "no-store";
        // Retain the origin on native form POSTs without exposing the token path.
        http.Response.Headers["Referrer-Policy"] = "strict-origin";
        var pending = await invitations.FindAsync(token, http.RequestAborted);
        var slice = InvitationView.Create(new(new("Organization invitation", DocumentModel.NonceFor(http)), Csrf.Token(http), token,
            pending, await sessions.ReadAsync(http), error));
        slice.StatusCode = pending is null ? 404 : status;
        return slice;
    }
}
