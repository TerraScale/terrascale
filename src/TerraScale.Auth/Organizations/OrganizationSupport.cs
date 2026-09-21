namespace TerraScale.Auth.Organizations;

using System.Net;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.DotNet;

internal static class OrganizationSupport
{
    // ---- Helpers ---------------------------------------------------------------------------

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Public origin without a trailing slash (Uri.ToString() always appends one).</summary>
    internal static string Origin(AuthOptions options) => options.PublicOrigin.ToString().TrimEnd('/');

    internal static bool IsValidRole(string? role) =>
        !string.IsNullOrEmpty(role) && role.Trim().Length is >= 1 and <= 64;

    internal static async Task<Organization?> FindOrganizationAsync(AuthStore store, string instanceId, string orgId, CancellationToken ct)
    {
        var organization = await store.GetOrganizationAsync(orgId, ct);
        return organization is not null && organization.InstanceId == instanceId ? organization : null;
    }

    internal static async Task<OrgMembership?> FindMembershipAsync(AuthStore store, string instanceId, string orgId, string membershipId, CancellationToken ct)
    {
        var membership = await store.GetMembershipAsync(membershipId, ct);
        return membership is not null && membership.InstanceId == instanceId && membership.OrgId == orgId ? membership : null;
    }

    internal static Task AuditAsync(AuthStore store, string instanceId, string type, string? actorId, Dictionary<string, object> data, CancellationToken ct) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Type = type,
            ActorId = actorId,
            Data = data,
            CreatedAt = Now(),
        }, ct);

    internal static object Render(Organization organization, string instanceId) =>
        new { id = organization.Id, name = organization.Name, slug = organization.SlugKey[(instanceId.Length + 1)..], created_at = organization.CreatedAt };

    internal static object Render(OrgMembership membership) =>
        new { id = membership.Id, organization_id = membership.OrgId, user_id = membership.UserId, role = membership.Role, created_at = membership.CreatedAt };

    internal static object Render(Invitation invitation) =>
        new { id = invitation.Id, organization_id = invitation.OrgId, email = invitation.Email, role = invitation.Role, status = invitation.Status, expires_at = invitation.ExpiresAt, created_at = invitation.CreatedAt };
}
