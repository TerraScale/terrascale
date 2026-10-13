
namespace TerraScale.Dashboard.Features.Accounts;

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
    public List<string> OrganizationIds { get; set; } = [];
    public string ActiveOrganizationId { get; set; } = "";
    public string ActiveProjectId { get; set; } = "";
    public List<NotificationRecord> Notifications { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

// Canonical, create-only subject ownership. The private document key is the
// subject digest; this public identity survives source-state migration.
public sealed record AccountIdentityBinding(string UserId, string AccountId);

public sealed class OrganizationRecord
{
    public const string FreePlanId = "3BN8V1JQWTB3R3NSZ26SERTHWT";
    public const string StarterPlanId = "443RCMMF70MNP6S1QMT230NF99";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "personal";
    public string Slug { get; set; } = "";
    public string Status { get; set; } = "active";
    public string OwnerUserId { get; set; } = "";
    public string PlanId { get; set; } = FreePlanId;
    public string BillingStatus { get; set; } = "active";
    public bool PaymentMethodValidated { get; set; }
    public long SpendCapCents { get; set; }
    public bool PaidExcess { get; set; }
    public bool Archived { get; set; }
    public bool DeletionRequested { get; set; }
    public InternalBillingPolicy? InternalBilling { get; set; }
    public Dictionary<string, InternalBillingAlert> InternalBillingAlerts { get; set; } = [];
    public string PendingOwnerUserId { get; set; } = "";
    public DateTimeOffset? PendingOwnerExpiresAt { get; set; }
    public Dictionary<string, MemberRecord> Members { get; set; } = [];
    public Dictionary<string, ProjectRecord> Projects { get; set; } = [];
    public List<AuditRecord> AuditEvents { get; set; } = [];
    public Dictionary<string, InvitationRecord> Invitations { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public bool ContainsMember(string userId) => Status == "active" && !Archived &&
        Members.TryGetValue(userId, out var member) && member.Status == "active";
    public string RoleFor(string userId) => ContainsMember(userId) ? Members[userId].Role : "";
    public bool CanManage(string userId) => RoleFor(userId) is "owner" or "admin";
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

public sealed class ProjectRecord
{
    public string Id { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Region { get; set; } = "";
    public string BackupRegion { get; set; } = "";
    public List<string> Regions { get; set; } = [];
    public string Status { get; set; } = "active";
    public bool IsDefault { get; set; }
    public long SpendCapCents { get; set; }
    public OperationLimits OperationLimits { get; set; } = new();
    public Dictionary<string, DatabaseRecord> Databases { get; set; } = [];
    public Dictionary<string, SharedResultConfiguration> SharedResultConfigs { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
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
        (ProjectId == "" || ProjectId == context.Project?.Id);
}

public sealed record AuditRecord(string Id, string Actor, string Action, string Target, string TargetType, DateTimeOffset OccurredAt, string Source = "dashboard", string? Risk = null);

public sealed class InvitationRecord
{
    public string Id { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string Email { get; set; } = "";
    public string Role { get; set; } = "member";
    public string TokenHash { get; set; } = "";
    public string RespondedByUserId { get; set; } = "";
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
}

public sealed class AccountAccessException(string message, int statusCode = StatusCodes.Status403Forbidden) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
