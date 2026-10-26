using System.Net;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

public sealed partial class TsAuthPlatformJourneyTests
{
    // PlatformDirectory.ProjectMembersAsync: current effective role; only explicit grants, ordered by user ID.
    private sealed partial class PlatformProducer
    {
        private HttpResponseMessage? ProjectGrantRoute(Organization org, (string UserId, string Email) caller,
            string method, string[] parts, string query, string body)
        {
            if (method != "GET" || parts[2..] is not ["projects", var projectId, "members"]) return null;
            var project = org.Projects.SingleOrDefault(item => item.Id == projectId);
            if (project is null || EffectiveRole(org, project, caller.UserId) is null)
                return Error(HttpStatusCode.NotFound, "not_found", "Project not found.");
            return Page(org.Collaborators.Where(item => item.Value.ContainsKey(projectId)).OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => (object)new
                {
                    user_id = item.Key,
                    email = org.Members.GetValueOrDefault(item.Key)?.Email ?? (item.Key == caller.UserId ? caller.Email : "collaborator@example.test"),
                    role = item.Value[projectId],
                    granted_at = project.CreatedAt
                }), query);
        }
    }
}
