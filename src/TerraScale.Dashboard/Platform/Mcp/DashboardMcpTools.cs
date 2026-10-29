using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TerraScale.Dashboard.Platform.Mcp;

// Tools invoke the same feature services as native forms. The HTTP transport
// authenticates each request and does not retain any connection state.
[McpServerToolType]
public sealed class DashboardMcpTools(IHttpContextAccessor http, AccountService accounts, TimeProvider time)
{
    private readonly IHttpContextAccessor http = http;
    private readonly AccountService accounts = accounts;
    private readonly TimeProvider time = time;

    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private AccountContext Account() => McpScope.Account(http.HttpContext);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;

    [McpServerTool(Name = "get_context", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("Get your account, accessible organizations and current project selection.")]
    public async Task<DashboardMcpContext> Context()
    {
        var context = await accounts.GetContextAsync(Account().Session, Cancellation);
        return new(new(context.UserId, context.Account.Name, context.Account.Email, null),
            new(context.Account.ActiveOrganizationId, context.Account.ActiveProjectId),
            [.. context.Organizations.Select(org => OrganizationSummary(context with { Organization = org, Project = null }))]);
    }

    [McpServerTool(Name = "get_organization", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("Read the selected organization's details and projects.")]
    public async Task<DashboardMcpOrganizationDetails> Organization(string? organizationId = null, string? projectId = null)
        => OrganizationDetails(await AccountScope.RefreshAsync(accounts, Scope(organizationId, projectId), Cancellation));

    [McpServerTool(Name = "select_organization", UseStructuredContent = true, Idempotent = true, Destructive = false, OpenWorld = false), Description("Select an accessible organization workspace, including one reached through a project grant.")]
    public async Task<DashboardMcpSelection> SelectOrganization(string organizationId)
    {
        var scoped = Scope(organizationId, null);
        var context = await AccountScope.RefreshAsync(accounts, scoped, Cancellation, requireProject: scoped.Role.Length == 0);
        await accounts.SwitchOrganizationAsync(context.Session, organizationId, Cancellation);
        var account = (await accounts.GetContextAsync(context.Session, Cancellation)).Account;
        return new(account.ActiveOrganizationId, account.ActiveProjectId);
    }

    [McpServerTool(Name = "select_project", UseStructuredContent = true, Idempotent = true, Destructive = false, OpenWorld = false), Description("Select a project you can access in the dashboard.")]
    public async Task<DashboardMcpSelection> SelectProject(string projectId)
    {
        var context = await accounts.GetContextAsync(Account().Session, Cancellation, projectId: projectId);
        context = await AccountScope.RefreshAsync(accounts, context, Cancellation, requireProject: true);
        if (context.Organization!.MfaRequired) throw new AccountAccessException("Organization is unavailable.", 404);
        var selected = await accounts.SwitchProjectAsync(context.Session, projectId, Cancellation);
        return new(selected.Account.ActiveOrganizationId, selected.Account.ActiveProjectId);
    }

    [McpServerTool(Name = "create_organization", UseStructuredContent = true, Destructive = false, OpenWorld = false), Description("Create a business organization and select it for billing setup.")]
    public async Task<DashboardMcpOrganization> CreateOrganization(string name)
    {
        var session = Account().Session;
        var org = await accounts.CreateBusinessAsync(session, name, Cancellation);
        var context = await accounts.GetContextAsync(session, Cancellation);
        return OrganizationSummary(await AccountScope.RefreshAsync(accounts, context with { Organization = org, Project = null }, Cancellation));
    }

    private DashboardMcpOrganizationDetails OrganizationDetails(AccountContext context)
    {
        var org = context.Organization!;
        return new(org.Id, org.Name, org.Kind, org.Status, org.RoleFor(context.UserId), org.PlanId,
            PlanCatalog.For(org)?.Name ?? "", org.BillingStatus,
            org.CanManage(context.UserId), org.CanProvision(context.UserId), org.DeletionRequested,
            [.. org.Projects.Values.OrderBy(project => project.Name, StringComparer.Ordinal).Select(project =>
                ProjectSummary(context, project))],
            org.Slug, org.CreatedAt == DateTimeOffset.MinValue ? null : org.CreatedAt,
            org.Members.Values.Count(member => member.Status == "active"),
            org.Invitations.Values.Count(invitation => invitation.Status == "pending" && invitation.ExpiresAt > time.GetUtcNow()),
            org.Kind == "business" && org.OwnerUserId != context.UserId,
            org.PendingOwnerUserId == context.UserId && org.PendingOwnerExpiresAt > time.GetUtcNow(),
            string.IsNullOrEmpty(org.PendingOwnerUserId) ? null : org.PendingOwnerUserId,
            string.IsNullOrEmpty(org.PendingOwnerUserId) ? null : org.PendingOwnerExpiresAt);
    }

    private static DashboardMcpOrganization OrganizationSummary(AccountContext context)
    {
        var org = context.Organization!;
        var member = org.ContainsMember(context.UserId);
        return new(org.Id, org.Name, member ? org.Kind : null, member ? org.Status : null, member ? org.RoleFor(context.UserId) : null,
            member ? org.PlanId : null, member ? PlanCatalog.For(org)?.Name ?? "" : null, member ? org.BillingStatus : null,
            org.CanManage(context.UserId), org.CanProvision(context.UserId), member ? org.DeletionRequested : null,
            [.. org.Projects.Values.OrderBy(project => project.Name, StringComparer.Ordinal).Select(project =>
                ProjectSummary(context, project))]);
    }

    private static DashboardMcpProject ProjectSummary(AccountContext context, ProjectRecord project) =>
        new(project.Id, project.Name, project.Region, project.BackupRegion, [.. project.Regions], project.IsDefault, project.Databases.Count,
            PlatformDatabases.IsPlatform(context.Organization?.Id) ? "viewer" : project.Role,
            (context with { Project = project }).CanWriteProjectData);
}
