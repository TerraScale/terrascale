using System.ComponentModel;
using System.Text.Json.Serialization;

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

public sealed record DashboardMcpOrganizationDetails(string Id, string Name, string Kind, string Status, string Role,
    string PlanId, string PlanName, string BillingStatus, bool CanManage, bool CanProvision, bool DeletionRequested,
    IReadOnlyList<DashboardMcpProject> Projects, string Slug,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? CreatedAt,
    int MemberCount, int PendingInvitations, bool CanLeave, bool CanConfirmOwnership,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PendingOwnerUserId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? PendingOwnerExpiresAt = null);
