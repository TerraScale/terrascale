using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E
{
    // An admitted owner may change one existing nonowner provider membership.
    // The production native pages and MCP tools must read that provider role afresh.
    public static class OrganizationProviderScenario
    {
        public static void MapRoutes(WebApplication app)
        {
            app.MapPost("/__test/organization/members/role",
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

                if (context.Organization is null || context.Role != "owner" || context.Organization.OwnerUserId != context.UserId || context.Organization.DeletionRequested)
                {
                    return Results.StatusCode(403);
                }

                var userId = form["userId"].ToString();
                var role = form["role"].ToString();
                if (role is not ("member" or "admin"))
                {
                    return Results.BadRequest();
                }

                lock (workspace.Gate)
                {
                    var organization = workspace.Organizations.GetValueOrDefault(context.Organization.Id);
                    if (organization is null || organization.OwnerUserId != context.UserId || workspace.ScopedRole(context.UserId, organization.Id) != "owner")
                    {
                        return Results.StatusCode(403);
                    }

                    var member = organization.Members.GetValueOrDefault(userId);
                    if (member is null || member.Status != "active" || userId == context.UserId || member.Role == "owner")
                    {
                        return Results.NotFound();
                    }

                    member.Role = role;
                }
                return Results.NoContent();
            });
        }
    }
}
