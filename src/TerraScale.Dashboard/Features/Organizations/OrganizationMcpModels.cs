using System.Text.Json.Serialization;
using System.ComponentModel;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed record OrganizationDone(bool Ok);
    public sealed record OrganizationProjectResult(string Id, string OrganizationId, string Name, string Region, string BackupRegion, IReadOnlyList<string> Regions);
    public sealed record OrganizationMemberView(string Id, string Name, string Email, string Role, bool IsOwner, bool IsSelf, bool CanEdit, string JoinedAt);
    public sealed record OrganizationRoleCounts(int Owner, int Member, int Viewer, int Admin);
    public sealed record OrganizationMemberList(IReadOnlyList<OrganizationMemberView> Members,
        [property: Description("Active members by role across the whole organization.")] OrganizationRoleCounts RoleCounts,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor, bool HasMore, int Limit);
    public sealed record OrganizationInvitationView(string Id, string Email, string Role, string Status, string ExpiresAt, bool Open);
    public sealed record OrganizationInvitationList(IReadOnlyList<OrganizationInvitationView> Invitations, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor, bool HasMore, int Limit);
    public sealed record OrganizationIssuedInvitation(OrganizationInvitationView Invitation,
        [property: Description("One-time invitation link path. Prefix it with the dashboard origin and share it with the invitee; it is not shown again.")] string InviteUrl);
    public sealed record OrganizationInvitationPreview(string OrganizationId, string OrganizationName,
        [property: Description("The invited email address; only that account can respond.")] string Email, string Role, DateTimeOffset ExpiresAt,
        [property: Description("Whether the caller's email matches the invitation.")] bool CanRespond);
    public sealed record OrganizationInvitationResponse(string OrganizationId, string OrganizationName, string Action);
    public sealed record OrganizationAuditEventView(string Id, DateTimeOffset OccurredAt, string Actor,
        [property: Description("user or service.")] string ActorType,
        [property: Description("Raw action code, such as api_key.created.")] string Action, string ActionLabel, string Target,
        string TargetType, string Source,
        [property: Description("info, warn, critical, or unknown when the provider reports no severity.")] string Severity,
        [property: Description("success or failure.")] string Outcome,
        [property: Description("Client IP of the request that made the change; empty when the producer reports none.")] string Ip);
    public sealed record OrganizationAuditEvents(IReadOnlyList<OrganizationAuditEventView> Events,
        [property: Description("Events matching the filters across all pages.")] int Matching, int Critical, int Warn, int Info,
        [property: Description("Matching events whose severity the provider does not report.")] int Unknown,
        int Page, int PageCount, string Range, string Severity,
        [property: Description("Action codes present in the retained history, for the action filter.")] IReadOnlyList<string> Actions,
        [property: Description("The plan's audit window in days: older events are not listed or exported.")] int RetentionDays);
}
