using System.ComponentModel;
using System.Text.Json.Serialization;
using TerraScale.Dashboard.Features.Profile;

namespace TerraScale.Dashboard.Platform.Mcp;

// Concrete return schemas preserve the Go wire shape on all supported MCP
// versions. A generic object return is wrapped by the official SDK on older
// protocol versions and hides these fields under an extra result property.
public sealed record DashboardMcpContext(DashboardMcpUser User, DashboardMcpSelection Selection,
    IReadOnlyList<DashboardMcpOrganization> Organizations);

public sealed record DashboardMcpUser(string Id, string Name, string Email,
    [property: Description("Authoritative platform-admin status is unavailable from the current producer contract; administration remains denied.")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    bool? PlatformAdmin);

public sealed record DashboardMcpSelection(string OrganizationId, string ProjectId);

public sealed record DashboardMcpOrganization(string Id, string Name, string Kind, string Status, string Role,
    string PlanId, string PlanName, string BillingStatus, bool CanManage, bool CanProvision, bool DeletionRequested,
    IReadOnlyList<DashboardMcpProject> Projects);

public sealed record DashboardMcpProject(string Id, string Name, string Region, string BackupRegion,
    IReadOnlyList<string> Regions, bool IsDefault, int Databases);

public sealed record DashboardMcpProfile(string UserId, string AccountId, string Name, string Email, string Phone, string Locale,
    string ActiveOrganizationId, string ActiveProjectId, string OrganizationId, string ProjectId, string OrganizationName,
    string Role, IReadOnlyList<string> Permissions,
    [property: Description("Real MFA status is unavailable from the current owning provider contract; null does not mean disabled.")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    bool? MfaEnabled,
    [property: Description("Real passkey records are unavailable from the current owning provider contract; null does not mean no passkeys.")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    IReadOnlyList<DashboardMcpPasskey>? Passkeys,
    IReadOnlyList<ProfileToken> AccessTokens);

public sealed record DashboardMcpPasskey(string Id, string Name, DateTimeOffset CreatedAt);

public sealed record DashboardMcpOrganizationDetails(string Id, string Name, string Kind, string Status, string Role,
    string PlanId, string PlanName, string BillingStatus, bool CanManage, bool CanProvision, bool DeletionRequested,
    IReadOnlyList<DashboardMcpProject> Projects, string Slug,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? CreatedAt,
    int MemberCount, int PendingInvitations, bool CanLeave, bool CanConfirmOwnership,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PendingOwnerUserId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? PendingOwnerExpiresAt = null);
public sealed record DashboardMcpSaved(bool Saved);
public sealed record DashboardMcpRevoked(bool Revoked);
public sealed record DashboardMcpAccessTokens(IReadOnlyList<ProfileToken> AccessTokens);
