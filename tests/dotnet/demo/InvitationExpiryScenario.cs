using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Organizations;

namespace TerraScale.Dashboard.E2E;

// The test host can age one existing owner-scoped invitation. Application
// preview, replacement and cancellation still use the production services.
public static class InvitationExpiryScenario
{
    public static void MapRoutes(WebApplication app) => app.MapPost("/__test/organization/invitations/expire",
        async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
            if (context.Organization is null || context.Role != "owner" || context.Organization.OwnerUserId != context.UserId)
                return Results.StatusCode(403);
            var id = form["invitationId"].ToString();
            if (!context.Organization.Invitations.TryGetValue(id, out var invitation) || invitation.OrganizationId != context.Organization.Id || invitation.Status != "pending")
                return Results.NotFound();
            var expiresAt = DateTimeOffset.UtcNow.AddDays(-1);
            TestOrganizationAccess.ExpireInvitation(context, id, expiresAt);
            await accounts.UpdateOrganizationAsync(context, org =>
            {
                if (org.RoleFor(context.UserId) != "owner" || org.OwnerUserId != context.UserId || org.DeletionRequested)
                    throw new AccountAccessException("Organization ownership is required.");
                if (!org.Invitations.TryGetValue(id, out var current) || current.OrganizationId != org.Id || current.Status != "pending")
                    throw new AccountAccessException("Invitation changed.", 409);
                current.ExpiresAt = expiresAt;
            }, http.RequestAborted);
            return Results.NoContent();
        });
}
