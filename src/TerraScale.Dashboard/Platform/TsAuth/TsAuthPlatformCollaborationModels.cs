using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed record TsAuthPlatformCreateInvitation(
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("project_id")] string? ProjectId = null);

    public sealed record TsAuthPlatformInvitationToken([property: JsonPropertyName("token")] string? Token)
    {
        public override string ToString()
        {
            return "TsAuthPlatformInvitationToken { sensitive fields redacted }";
        }
    }

    public sealed record TsAuthPlatformStartOwnershipTransfer([property: JsonPropertyName("user_id")] string? UserId);

    public sealed record TsAuthPlatformInvitation(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("invited_by")] string InvitedBy,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
        [property: JsonPropertyName("status")] string Status);

    public sealed record TsAuthPlatformCreatedInvitation(
        [property: JsonPropertyName("invitation")] TsAuthPlatformInvitation Invitation,
        [property: JsonPropertyName("token")] string Token)
    {
        public override string ToString()
        {
            return "TsAuthPlatformCreatedInvitation { sensitive fields redacted }";
        }
    }

    public sealed record TsAuthPlatformNamed(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name);

    public sealed record TsAuthPlatformInvitationLookup(
        [property: JsonPropertyName("organization")] TsAuthPlatformNamed Organization,
        [property: JsonPropertyName("project")] TsAuthPlatformNamed? Project,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

    public sealed record TsAuthPlatformAcceptedInvitation(
        [property: JsonPropertyName("organization_id")] string OrganizationId,
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("role")] string Role);

    public sealed record TsAuthPlatformOwnershipTransfer(
        [property: JsonPropertyName("from_user_id")] string FromUserId,
        [property: JsonPropertyName("to_user_id")] string ToUserId,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

    public sealed record TsAuthPlatformAuditEvent(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("actor")] string Actor,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("target")] string? Target,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt);
}
