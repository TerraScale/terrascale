using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Common.Accounts
{
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
        public string PersonalOrganizationId { get; set; } = "";
        public DateTimeOffset? PersonalClaimUntil { get; set; }
        public string ActiveOrganizationId { get; set; } = "";
        public string ActiveProjectId { get; set; } = "";
        public List<NotificationRecord> Notifications { get; set; } = [];
        public DateTimeOffset CreatedAt { get; set; }
    }

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
        public DateTimeOffset? PaymentGraceUntil { get; set; }
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
        public Dictionary<string, ProjectRecord> Projects { get; set; } = [];
        public List<AuditRecord> AuditEvents { get; set; } = [];
        public int? AuditRetentionDays { get; set; }
        [JsonIgnore] public Dictionary<string, InvitationRecord> Invitations { get; set; } = [];
        public Dictionary<string, InvitationReservation> InvitationReservations { get; set; } = [];
        public Dictionary<string, string> PendingProjectDeletions { get; set; } = [];
        public HashSet<string> DeletedProjectIds { get; set; } = [];
        [JsonIgnore] public DateTimeOffset CreatedAt { get; set; }
        [JsonIgnore] public bool MfaRequired { get; set; }
        public bool ContainsMember(string userId)
        {
            return Status == "active" && !Archived &&
            Members.TryGetValue(userId, out var member) && member.Status == "active";
        }

        public string RoleFor(string userId)
        {
            return !ContainsMember(userId) ? "" : PlatformDatabases.IsPlatform(Id) ? "viewer" : Members[userId].Role;
        }

        public bool CanManage(string userId)
        {
            return RoleFor(userId) is "owner" or "admin";
        }

        public bool CanWrite(string userId)
        {
            return RoleFor(userId) is "owner" or "admin" or "member";
        }

        public bool CanProvision(string userId)
        {
            return !DeletionRequested && CanManage(userId) && (Kind == "personal" ? OwnerUserId == userId : BillingStatus == "active");
        }
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
        [JsonIgnore] public string? Role { get; set; }
        [JsonIgnore] public string OrganizationId { get; set; } = "";
        [JsonIgnore] public string Name { get; set; } = "";
        [JsonIgnore] public string Slug { get; set; } = "";
        public List<string> Regions { get; set; } = [];

        public string Region => Regions.Count > 0 ? Regions[0] : "";
        public string Status { get; set; } = "active";
        public bool IsDefault { get; set; }
        public long SpendCapCents { get; set; }
        public OperationLimits OperationLimits { get; set; } = new();
        public Dictionary<string, DatabaseRecord> Databases { get; set; } = [];
        [JsonIgnore] public DateTimeOffset CreatedAt { get; set; }
    }

    public sealed class DatabaseRecord
    {
        public DateTimeOffset? ExpiresAt { get; set; }
        public string Id { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? PreviousName { get; set; }
        public string? ConflictOf { get; set; }
        public string Region { get; set; } = "";
        public List<string> Regions { get; set; } = [];
        public bool AllRegions { get; set; }
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
        public bool VisibleTo(AccountContext context)
        {
            if (Type.StartsWith("allowance-", StringComparison.Ordinal) &&
                (OrganizationId == "" || context.Organization?.RoleFor(context.UserId) != "owner"))
            {
                return false;
            }

            if (OrganizationId == "" && ProjectId == "")
            {
                return true;
            }

            var organization = context.Organization;
            if (organization is null || OrganizationId != organization.Id || organization.MfaRequired ||
                organization.Status != "active" || organization.Archived || !context.Organizations.Contains(organization))
            {
                return false;
            }

            if (ProjectId == "")
            {
                return organization.ContainsMember(context.UserId);
            }

            var project = context.Project;
            return project is { Status: "active" } && ProjectId == project.Id && project.OrganizationId == organization.Id &&
                organization.Projects.GetValueOrDefault(project.Id) == project && (organization.ContainsMember(context.UserId) ||
                    !PlatformDatabases.IsPlatform(organization.Id) && project.Role is "viewer" or "member" or "admin");
        }
    }

    public sealed record AuditRecord(string Id, string Actor, string Action, string Target, string TargetType, DateTimeOffset OccurredAt, string Source = "dashboard", string? Risk = null, string? Ip = null)
    {
        public string? Risk { get; init; } = Risk ?? (Source == "dashboard" ? DashboardAuditSeverity.ByAction.GetValueOrDefault(Action) : null);
    }

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
            ["billing.spend_cap_updated"] = "warning",
            ["member.role_changed"] = "warning",
            ["member.removed"] = "warning",
            ["ownership.requested"] = "warning",
            ["project.deleted"] = "warning",
            ["settings.audit_retention_updated"] = "warning",
            ["api_key.created"] = "warning",
            ["api_key.revoked"] = "warning",
            ["database.restored"] = "warning",
            ["database.renamed"] = "warning",
            ["billing.plan_changed"] = "warning",
            ["backup.destination_removed"] = "warning",
            ["database.deleted"] = "critical",
            ["backup.destination_updated"] = "critical",
            ["ownership.transferred"] = "critical",
            ["organization.deletion_requested"] = "critical",
            ["organization.deleted"] = "critical"
        };
    }

    public sealed class InvitationRecord
    {
        public string Id { get; set; } = "";
        public string OrganizationId { get; set; } = "";
        public string Email { get; set; } = "";
        public string Role { get; set; } = "member";
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
        private bool HasProjectAuthority => UserId == Session.UserId && !string.IsNullOrWhiteSpace(UserId) &&
            !string.IsNullOrWhiteSpace(Session.AccessToken) && Session.ExpiresAt > DateTimeOffset.UtcNow &&
            Organization is { Status: "active", Archived: false, DeletionRequested: false, MfaRequired: false } organization &&
            Organizations.Any(item => ReferenceEquals(item, organization)) &&
            TerraScale.Dashboard.Common.Accounts.TerraScaleIds.IsValid(organization.Id) &&
            Project is { Status: "active", Role: "member" or "admin" } project && TerraScale.Dashboard.Common.Accounts.TerraScaleIds.IsValid(project.Id) &&
            project.OrganizationId == organization.Id && ReferenceEquals(organization.Projects.GetValueOrDefault(project.Id), project);
        public bool CanWriteProjectData => HasProjectAuthority && Organization is { } organization && !PlatformDatabases.IsPlatform(organization.Id);
        public bool CanSetOrganizationPicker => Project?.Role == "admin" && HasProjectAuthority && Project.Id == PlatformDatabases.AuthProjectId;
        public bool CanManageProject => Project?.Role == "admin" && CanWriteProjectData;
        public bool CanManageKeys => CanManage || CanWriteProjectData;
        public bool CanManageProjectCidrs => CanManage || CanManageProject;
        [System.Text.Json.Serialization.JsonIgnore] public ServerTiming.Mark? Loaded { get; init; }
    }

    public sealed record InvitationReservation(string Email, DateTimeOffset ReservedAt, string InvitationId = "")
    {
        public string? ProjectId { get; init; }
        public IReadOnlyList<string> Known { get; init; } = [];
    }

    public sealed record CreatedInvitation(InvitationRecord Invitation, string Token)
    {
        public override string ToString()
        {
            return "Created invitation [REDACTED]";
        }
    }

    public sealed record OwnershipTransferRecord(string FromUserId, string ToUserId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

    public sealed class AccountAccessException(string message, int statusCode = StatusCodes.Status403Forbidden, string code = "") : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
        public string Code { get; } = code;
    }
}
