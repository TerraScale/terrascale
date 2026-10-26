using System.Text.Json.Serialization;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Shared.Accounts;

public sealed class AccountRecord
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Locale { get; set; } = "en";
    public bool OnboardingCompleted { get; set; }
    public string OnboardingStep { get; set; } = "account-information";
    // Organizations the dashboard has shown this account. ts-auth decides membership; this list never authorizes.
    public List<string> OrganizationIds { get; set; } = [];
    // The ts-auth organization the dashboard created as this account's Personal workspace.
    public string PersonalOrganizationId { get; set; } = "";
    // Lease held while one replica creates that organization, so concurrent first sign-ins create one.
    public DateTimeOffset? PersonalClaimUntil { get; set; }
    public string ActiveOrganizationId { get; set; } = "";
    public string ActiveProjectId { get; set; } = "";
    public List<NotificationRecord> Notifications { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

// Canonical, create-only subject ownership. The private document key is the
// subject digest; this public identity survives source-state migration.
public sealed record AccountIdentityBinding(string UserId, string AccountId);

// No profile or identity data survives; the erasure marker prevents create-only
// writers with an already-loaded session from resurrecting these private keys.
public sealed record DeletionFence(string ErasureId);

// Dashboard-owned state of a ts-auth organization, keyed by its ts-auth ID. The
// [JsonIgnore] members are ts-auth's: AccountService fills them from ts-auth on
// every read and before every write, and they are never stored.
public sealed class OrganizationRecord
{
    public const string FreePlanId = "3BN8V1JQWTB3R3NSZ26SERTHWT";
    public const string StarterPlanId = "443RCMMF70MNP6S1QMT230NF99";
    public string Id { get; set; } = "";
    [JsonIgnore] public string Name { get; set; } = "";
    public string Kind { get; set; } = "personal";
    [JsonIgnore] public string Slug { get; set; } = "";
    public string Status { get; set; } = "active";
    [JsonIgnore] public string OwnerUserId { get; set; } = "";
    public string PlanId { get; set; } = FreePlanId;
    public string BillingStatus { get; set; } = "active";
    public bool PaymentMethodValidated { get; set; }
    public long SpendCapCents { get; set; }
    public bool PaidExcess { get; set; }
    public bool Archived { get; set; }
    public bool DeletionRequested { get; set; }
    public InternalBillingPolicy? InternalBilling { get; set; }
    public Dictionary<string, InternalBillingAlert> InternalBillingAlerts { get; set; } = [];
    [JsonIgnore] public string PendingOwnerUserId { get; set; } = "";
    [JsonIgnore] public DateTimeOffset? PendingOwnerExpiresAt { get; set; }
    [JsonIgnore] public Dictionary<string, MemberRecord> Members { get; set; } = [];
    // Dashboard settings per ts-auth project id; only ts-auth's current projects are kept.
    public Dictionary<string, ProjectRecord> Projects { get; set; } = [];
    public List<AuditRecord> AuditEvents { get; set; } = [];
    // The owner's audit retention (1 to 90 days); the plan's window caps it, and none means the plan's window.
    public int? AuditRetentionDays { get; set; }
    [JsonIgnore] public Dictionary<string, InvitationRecord> Invitations { get; set; } = [];
    // Plan member allowance: invitations this dashboard is creating at ts-auth, by operation id,
    // until ts-auth lists them. Every change goes through the document's compare-and-set.
    public Dictionary<string, InvitationReservation> InvitationReservations { get; set; } = [];
    public Dictionary<string, string> PendingProjectDeletions { get; set; } = [];
    // Terminal project IDs contain no names or settings and must survive stale auth snapshots.
    public HashSet<string> DeletedProjectIds { get; set; } = [];
    [JsonIgnore] public DateTimeOffset CreatedAt { get; set; }
    // ts-auth refused this view: the organization requires two-factor and the caller has not enrolled.
    [JsonIgnore] public bool MfaRequired { get; set; }
    public bool ContainsMember(string userId) => Status == "active" && !Archived &&
        Members.TryGetValue(userId, out var member) && member.Status == "active";
    // The platform organization is read-only in the dashboard whatever ts-auth grants: every write gate reads this.
    public string RoleFor(string userId) => !ContainsMember(userId) ? "" : PlatformDatabases.IsPlatform(Id) ? "viewer" : Members[userId].Role;
    public bool CanManage(string userId) => RoleFor(userId) is "owner" or "admin";
    // ts-auth's viewer role reads only.
    public bool CanWrite(string userId) => RoleFor(userId) is "owner" or "admin" or "member";
    public bool CanProvision(string userId) => !DeletionRequested && CanManage(userId) && (Kind == "personal" ? OwnerUserId == userId : BillingStatus == "active");
}

public sealed class MemberRecord
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Role { get; set; } = "member";
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; }
    public DateOnly? JoinedOn { get; set; }
}

// Dashboard settings of a ts-auth project; the [JsonIgnore] members come from ts-auth.
public sealed class ProjectRecord
{
    public string Id { get; set; } = "";
    // The caller's effective project role, replaced from ts-auth on each request; never stored authority.
    [JsonIgnore] public string? Role { get; set; }
    [JsonIgnore] public string OrganizationId { get; set; } = "";
    [JsonIgnore] public string Name { get; set; } = "";
    [JsonIgnore] public string Slug { get; set; } = "";
    public string Region { get; set; } = "";
    public string BackupRegion { get; set; } = "";
    public List<string> Regions { get; set; } = [];
    public string Status { get; set; } = "active";
    public bool IsDefault { get; set; }
    public long SpendCapCents { get; set; }
    public OperationLimits OperationLimits { get; set; } = new();
    public Dictionary<string, DatabaseRecord> Databases { get; set; } = [];
    public Dictionary<string, SharedResultConfiguration> SharedResultConfigs { get; set; } = [];
    [JsonIgnore] public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SharedResultConfiguration
{
    public string Id { get; set; } = "";
    public string DatabaseId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public string Transport { get; set; } = "";
    public string Status { get; set; } = "saved";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DatabaseRecord
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Region { get; set; } = "";
    public List<string> Regions { get; set; } = [];
    // Every serving region holds a copy, including regions that join later; Regions then lists only the home.
    public bool AllRegions { get; set; }
    // Every persisted placement change advances this, including saves that repeat the existing values.
    public long PlacementRevision { get; set; }
    public string Endpoint { get; set; } = "";
    public string Status { get; set; } = "active";
    public long SpendCapCents { get; set; }
    public OperationLimits OperationLimits { get; set; } = new();
    public int BackupRetentionDays { get; set; }
    public string? BranchedFrom { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class OperationLimits
{
    public long ReadLimit { get; set; }
    public long WriteLimit { get; set; }
    public long ReplicationLimit { get; set; }
}

public sealed class NotificationRecord
{
    public string Id { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Type { get; set; } = "";
    public string BillingMonth { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Severity { get; set; } = "info";
    public string Status { get; set; } = "unread";
    public string Url { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public bool Unread => Status.Equals("unread", StringComparison.OrdinalIgnoreCase);
    public bool VisibleTo(AccountContext context) => (OrganizationId == "" || OrganizationId == context.Organization?.Id) &&
        (ProjectId == "" || ProjectId == context.Project?.Id) &&
        (!Type.StartsWith("allowance-", StringComparison.Ordinal) || context.Organization?.RoleFor(context.UserId) == "owner");
}

// Ip is the client address of the request that recorded a dashboard event; events stored before it was recorded,
// and other producers' events, have none.
public sealed record AuditRecord(string Id, string Actor, string Action, string Target, string TargetType, DateTimeOffset OccurredAt, string Source = "dashboard", string? Risk = null, string? Ip = null)
{
    // The dashboard knows what it records: its own events carry their catalog severity. Other
    // producers' events keep what they reported, and a missing value stays "Not reported".
    public string? Risk { get; init; } = Risk ?? (Source == "dashboard" ? DashboardAuditSeverity.ByAction.GetValueOrDefault(Action) : null);
}

// The one severity map for audit events the dashboard records. A journey checks that
// every action the dashboard source records appears here.
public static class DashboardAuditSeverity
{
    public static IReadOnlyDictionary<string, string> ByAction { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["organization.renamed"] = "info",
        ["session.created"] = "info",
        ["database.created"] = "info",
        ["project.created"] = "info",
        ["project.renamed"] = "info",
        ["member.left"] = "info",
        ["ownership.canceled"] = "info",
        ["billing.internal_report_cap_updated"] = "warning",
        ["member.role_changed"] = "warning",
        ["member.removed"] = "warning",
        ["ownership.requested"] = "warning",
        ["project.deleted"] = "warning",
        ["settings.audit_retention_updated"] = "warning",
        ["api_key.created"] = "warning",
        ["api_key.revoked"] = "warning",
        ["database.restored"] = "warning",
        ["billing.plan_changed"] = "warning",
        ["backup.destination_removed"] = "warning",
        ["database.deleted"] = "critical",
        ["backup.destination_updated"] = "critical",
        ["ownership.transferred"] = "critical",
        ["organization.deletion_requested"] = "critical",
        ["organization.deleted"] = "critical"
    };
}

// A ts-auth invitation as the dashboard shows it; ts-auth stores it and its token.
public sealed class InvitationRecord
{
    public string Id { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string Email { get; set; } = "";
    public string Role { get; set; } = "member";
    // The project a project invitation grants; none for an organization invitation.
    public string? ProjectId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed record AccountContext(SessionRecord Session, AccountRecord Account,
    IReadOnlyList<OrganizationRecord> Organizations, OrganizationRecord? Organization, ProjectRecord? Project)
{
    public string UserId => Account.UserId;
    public string Role => Organization?.RoleFor(UserId) ?? "";
    public bool CanManage => Organization?.CanManage(UserId) ?? false;
    public bool CanWrite => Organization?.CanWrite(UserId) ?? false;
    // When this request loaded the context from ts-auth; AccountScope reuses it while nothing has changed since.
    [System.Text.Json.Serialization.JsonIgnore] public ServerTiming.Mark? Loaded { get; init; }
}

// One in-flight invitation: its email, when the slot was taken and, once ts-auth answered, its id.
public sealed record InvitationReservation(string Email, DateTimeOffset ReservedAt, string InvitationId = "")
{
    // The project of a project invitation; none for an organization invitation.
    public string? ProjectId { get; init; }
    // ts-auth invitation ids for the email that cannot be this reservation's: listed when it was taken, or since
    // accounted for by another reservation.
    public IReadOnlyList<string> Known { get; init; } = [];
}

public sealed record CreatedInvitation(InvitationRecord Invitation, string Token)
{
    public override string ToString() => "Created invitation [REDACTED]";
}

public sealed record OwnershipTransferRecord(string FromUserId, string ToUserId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed class AccountAccessException(string message, int statusCode = StatusCodes.Status403Forbidden, string code = "") : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    // ts-auth's error code when ts-auth refused the change, for example name_taken.
    public string Code { get; } = code;
}
