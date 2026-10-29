using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E
{
    // The test host can age one existing owner-scoped invitation. Application
    // preview, replacement and cancellation still use the production services.
    public static class InvitationExpiryScenario
    {
        public static void MapRoutes(WebApplication app)
        {
            app.MapPost("/__test/organization/invitations/expire",
            async (HttpContext http, DashboardSessions sessions, AccountService accounts, TestWorkspace workspace) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form))
                {
                    return Results.StatusCode(403);
                }

                if (context.Organization is null || context.Role != "owner" || context.Organization.OwnerUserId != context.UserId)
                {
                    return Results.StatusCode(403);
                }

                workspace.ExpireInvitation(context, form["invitationId"].ToString(), DateTimeOffset.UtcNow.AddDays(-1));
                return Results.NoContent();
            });
        }
    }
}
