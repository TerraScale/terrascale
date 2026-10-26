using System.Net;
using Microsoft.AspNetCore.WebUtilities;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

public sealed partial class TsAuthPlatformJourneyTests
{
    private sealed partial class PlatformProducer
    {
        public void ClearProjectInvitations(string org, string project) { lock (gate) Find(org)!.Invitations.RemoveAll(invite => invite.ProjectId == project); }
        public string? InvitationProjectReply { get; set; }
        private HttpResponseMessage? ProjectInvitationRoute(Organization org, (string UserId, string Email) caller,
            string method, string[] parts, string query)
        {
            var parameters = QueryHelpers.ParseQuery(query);
            if (method != "GET" || parts[2..] is not ["invitations"] || !parameters.TryGetValue("project_id", out var projectId)) return null;
            var project = org.Projects.SingleOrDefault(item => item.Id == projectId);
            if (project is null || EffectiveRole(org, project, caller.UserId) is null) return Error(HttpStatusCode.NotFound, "not_found", "Project not found.");
            if (EffectiveRole(org, project, caller.UserId) != "admin") return Error(HttpStatusCode.Forbidden, "forbidden_role", "Needs project admin.");
            return Page(org.Invitations.Where(invite => invite.ProjectId == project.Id).OrderBy(invite => invite.Id, StringComparer.Ordinal)
                .Select(invite => (object)new
                {
                    id = invite.Id,
                    email = invite.Email,
                    role = invite.Role,
                    project_id = InvitationProjectReply ?? invite.ProjectId,
                    invited_by = invite.InvitedBy,
                    created_at = invite.CreatedAt,
                    expires_at = invite.ExpiresAt,
                    status = invite.Status == "pending" && invite.ExpiresAt <= DateTimeOffset.UtcNow ? "expired" : invite.Status
                }), query);
        }
    }
}
