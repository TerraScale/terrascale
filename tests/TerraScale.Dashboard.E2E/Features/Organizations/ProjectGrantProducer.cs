using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

public sealed partial class TsAuthPlatformJourneyTests
{
    // PlatformDirectory.ProjectMembersAsync: current effective role; only explicit grants, ordered by user ID.
    private sealed partial class PlatformProducer
    {
        private HttpResponseMessage? ProjectGrantRoute(Organization org, (string UserId, string Email) caller,
            string method, string[] parts, string query, string body)
        {
            if (parts[2..] is not ["projects", var projectId, "members", ..]) return null;
            var project = org.Projects.SingleOrDefault(item => item.Id == projectId);
            if (project is null || EffectiveRole(org, project, caller.UserId) is null)
                return Error(HttpStatusCode.NotFound, "not_found", "Project not found.");
            if (method == "GET" && parts.Length == 5) return Page(org.Collaborators.Where(item => item.Value.ContainsKey(projectId)).OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => (object)new
                {
                    user_id = item.Key,
                    email = org.Members.GetValueOrDefault(item.Key)?.Email ?? (item.Key == caller.UserId ? caller.Email : "collaborator@example.test"),
                    role = item.Value[projectId],
                    granted_at = project.CreatedAt
                }), query);
            if (parts[2..] is not ["projects", _, "members", var userId] || method is not ("PUT" or "DELETE")) return null;
            if (EffectiveRole(org, project, caller.UserId) != "admin") return Error(HttpStatusCode.Forbidden, "forbidden_role", "Needs project admin.");
            var existing = org.Collaborators.GetValueOrDefault(userId)?.ContainsKey(projectId) == true;
            // New grants need an organization member; an existing collaborator grant can be changed.
            if (method == "PUT")
            {
                if (!existing && !org.Members.ContainsKey(userId)) return Error(HttpStatusCode.NotFound, "not_found", "Unknown target.");
                using var json = JsonDocument.Parse(body);
                var role = json.RootElement.GetProperty("role").GetString()!;
                Require(role is "viewer" or "member" or "admin", "The body names a project role.");
                AddCollaborator(org.Id, userId, projectId, role);
                return Json(new { user_id = userId, email = org.Members.GetValueOrDefault(userId)?.Email ?? caller.Email, role, granted_at = project.CreatedAt });
            }
            if (!existing) return Error(HttpStatusCode.NotFound, "not_found", "No explicit grant.");
            RemoveGrant(org.Id, userId, projectId);
            return new(HttpStatusCode.NoContent);
        }
    }
}
