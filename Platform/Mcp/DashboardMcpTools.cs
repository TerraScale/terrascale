using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Features.Home;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Features.Profile;

namespace TerraScale.Dashboard.Platform.Mcp;

// Tools invoke the same feature services as native forms. The HTTP transport
// authenticates each request and does not retain any connection state.
[McpServerToolType]
public sealed partial class DashboardMcpTools
{
    private readonly IHttpContextAccessor http;
    private readonly AccountService accounts;
    private readonly DatabaseService databases;
    private readonly DataService data;
    private readonly IDatabaseBackend backend;
    private readonly IDocumentBackend documents;
    private readonly IHomeMeasurements measurements;
    private readonly AccessTokens tokens;
    private readonly StripeBilling billing;
    private readonly TimeProvider time;
    private readonly TerraScale.Dashboard.Features.ApiKeys.ApiKeyService keys;
    private readonly SavedQueryService saved;
    private readonly BackupService backups;

    public DashboardMcpTools(IHttpContextAccessor http, AccountService accounts, DatabaseService databases, DataService data, IDatabaseBackend backend, IDocumentBackend documents, IHomeMeasurements measurements, AccessTokens tokens, StripeBilling billing, TimeProvider time, TerraScale.Dashboard.Features.ApiKeys.ApiKeyService keys, SavedQueryService saved, BackupService backups)
    {
        this.http = http;
        this.accounts = accounts;
        this.databases = databases;
        this.data = data;
        this.backend = backend;
        this.documents = documents;
        this.measurements = measurements;
        this.tokens = tokens;
        this.billing = billing;
        this.time = time;
        this.keys = keys;
        this.saved = saved;
        this.backups = backups;
    }

    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private AccountContext Account() => http.HttpContext?.Items[DashboardMcp.ContextKey] as AccountContext ?? throw new AccountAccessException("Authentication is required.", 401);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;

    [McpServerTool(Name = "get_context", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("Get your account, accessible organizations and current project selection.")]
    public async Task<DashboardMcpContext> Context()
    {
        var context = await accounts.GetContextAsync(Account().Session, Cancellation);
        return new(new(context.UserId, context.Account.Name, context.Account.Email, null),
            new(context.Account.ActiveOrganizationId, context.Account.ActiveProjectId),
            context.Organizations.Select(org => new DashboardMcpOrganization(org.Id, org.Name, org.Kind, org.Status,
                org.RoleFor(context.UserId), org.PlanId, PlanCatalog.For(org)?.Name ?? "",
                org.BillingStatus, org.CanManage(context.UserId), org.CanProvision(context.UserId), org.DeletionRequested,
                org.Projects.Values.OrderBy(project => project.Name, StringComparer.Ordinal).Select(project =>
                    new DashboardMcpProject(project.Id, project.Name, project.Region, project.BackupRegion, project.Regions.ToArray(),
                        project.IsDefault, project.Databases.Count)).ToArray())).ToArray());
    }

    [McpServerTool(Name = "get_organization", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("Read the selected organization's details and projects.")]
    public async Task<DashboardMcpOrganizationDetails> Organization(string? organizationId = null, string? projectId = null)
        => OrganizationDetails(await AccountScope.RefreshAsync(accounts, Scope(organizationId, projectId), Cancellation));

    [McpServerTool(Name = "select_organization", UseStructuredContent = true, Idempotent = true, Destructive = false, OpenWorld = false), Description("Select an organization you belong to in the dashboard.")]
    public async Task<DashboardMcpSelection> SelectOrganization(string organizationId)
    {
        var context = await AccountScope.RefreshAsync(accounts, Scope(organizationId, null), Cancellation);
        await accounts.SwitchOrganizationAsync(context.Session, organizationId, Cancellation);
        var account = (await accounts.GetContextAsync(context.Session, Cancellation)).Account;
        return new(account.ActiveOrganizationId, account.ActiveProjectId);
    }

    [McpServerTool(Name = "select_project", UseStructuredContent = true, Idempotent = true, Destructive = false, OpenWorld = false), Description("Select a project you can access in the dashboard.")]
    public async Task<DashboardMcpSelection> SelectProject(string projectId)
    {
        var context = await accounts.GetContextAsync(Account().Session, Cancellation);
        var organizations = context.Organizations.Where(org => org.ContainsMember(context.UserId) &&
            org.Projects.TryGetValue(projectId, out var project) && project.OrganizationId == org.Id).ToArray();
        if (organizations.Length != 1) throw new AccountAccessException("Project is unavailable.", 404);
        var organizationId = organizations[0].Id;
        await accounts.UpdateAccountAsync(context.Session, account =>
        { account.ActiveOrganizationId = organizationId; account.ActiveProjectId = projectId; }, Cancellation);
        return new(organizationId, projectId);
    }

    [McpServerTool(Name = "create_organization", UseStructuredContent = true, Destructive = false, OpenWorld = false), Description("Create a business organization and select it for billing setup.")]
    public async Task<DashboardMcpOrganization> CreateOrganization(string name)
    {
        var session = Account().Session;
        var org = await accounts.CreateBusinessAsync(session, name, Cancellation);
        var context = await accounts.GetContextAsync(session, Cancellation);
        return OrganizationSummary(await AccountScope.RefreshAsync(accounts, context with { Organization = org, Project = null }, Cancellation));
    }

    [McpServerTool(Name = "get_profile", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("Read your profile and interface language.")]
    public async Task<DashboardMcpProfile> Profile(string? organizationId = null, string? projectId = null)
    {
        var current = Account();
        var context = current.Organization is null && string.IsNullOrEmpty(organizationId) && string.IsNullOrEmpty(projectId)
            ? await accounts.GetContextAsync(current.Session, Cancellation)
            : await AccountScope.RefreshAsync(accounts, Scope(organizationId, projectId), Cancellation);
        var permissions = new List<string>();
        if (context.Project is not null)
        {
            permissions.Add("Access selected project");
            if (context.CanWrite) permissions.Add("Write selected project data");
        }
        if (context.CanManage) permissions.Add("Manage organization resources");
        if (context.Organization?.CanProvision(context.UserId) == true) permissions.Add("Provision organization resources");
        return new(context.UserId, context.Account.Id, context.Account.Name, context.Account.Email, context.Account.Phone,
            context.Account.Locale, context.Account.ActiveOrganizationId, context.Account.ActiveProjectId,
            context.Organization?.Id ?? "", context.Project?.Id ?? "", context.Organization?.Name ?? "", context.Role,
            permissions, null, null, await tokens.ListAsync(context, Cancellation));
    }

    [McpServerTool(Name = "update_profile", UseStructuredContent = true, Idempotent = true, Destructive = false, OpenWorld = false), Description("Update your display name and optional phone number.")]
    public async Task<DashboardMcpSaved> UpdateProfile(string name = "", string? phone = null)
    {
        await ProfileChanges.SaveAsync(accounts, Account().Session, name, phone, Cancellation);
        return new(true);
    }

    [McpServerTool(Name = "set_language", UseStructuredContent = true, Idempotent = true, Destructive = false, OpenWorld = false), Description("Set your interface language to en or pt-BR.")]
    public async Task<DashboardMcpSaved> Language(string locale)
    {
        if (locale is not ("en" or "pt-BR")) throw new AccountAccessException("Choose an available language.", 422);
        await accounts.UpdateAccountAsync(Account().Session, account => account.Locale = locale, Cancellation);
        return new(true);
    }

    [McpServerTool(Name = "list_access_tokens", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("List live personal access token metadata without secrets.")]
    public async Task<DashboardMcpAccessTokens> ListAccessTokens() => new(await tokens.ListAsync(Account(), Cancellation));

    [McpServerTool(Name = "revoke_access_token", UseStructuredContent = true, Destructive = true, OpenWorld = false), Description("Revoke one of your personal access tokens immediately.")]
    public async Task<DashboardMcpRevoked> RevokeAccessToken(string tokenId) { await tokens.RevokeAsync(Account(), tokenId, Cancellation); return new(true); }

    [McpServerTool(Name = "get_overview", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("Read the selected project's measured operations and region health.")]
    public async Task<HomeMcpOverview> Overview(string range = "7d", string? organizationId = null, string? projectId = null)
    {
        var context = await AccountScope.RefreshAsync(accounts, Scope(organizationId, projectId), Cancellation);
        var normalized = range is "1h" or "24h" or "7d" or "30d" ? range : "7d";
        return HomeMcpPresentation.For(context, await measurements.ReadAsync(context, normalized, Cancellation), normalized);
    }

    [McpServerTool(Name = "list_plans", Title = "List plans", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = true, Idempotent = true), Description("List the paid plans an organization can subscribe to, with prices in cents, limits, and which plan is current.")]
    public Task<BillingMcpPlans> Plans(string? organizationId = null, string? projectId = null) => billing.McpPlansAsync(Scope(organizationId, projectId), Cancellation);

    [McpServerTool(Name = "get_billing", Title = "Get billing", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = true, Idempotent = true), Description("Show an organization's plan, billing interval and status, renewal date, scheduled or pending plan changes, checkout state, and (for the owner) payment method summary.")]
    public Task<BillingMcpSummary> Billing(string? organizationId = null, string? projectId = null) => billing.McpSummaryAsync(Scope(organizationId, projectId), Cancellation);

    [McpServerTool(Name = "list_invoices", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("List organization invoices newest first, ten per page, filtered by status or issued date.")]
    public async Task<BillingInvoiceList> Invoices(string status = "", string issued = "", int page = 1, string? organizationId = null, string? projectId = null)
    {
        var invoices = await billing.InvoicesAsync(Scope(organizationId, projectId), Cancellation);
        return InvoiceHistory.Page(invoices, status, issued, page, time.GetUtcNow());
    }

    [McpServerTool(Name = "create_checkout", Title = "Start plan checkout", OpenWorld = true, Destructive = false, UseStructuredContent = true), Description("Start a checkout that subscribes an organization without a subscription to a paid plan. Returns the URL the owner must open in a browser to pay; this tool never completes a payment. Owner only.")]
    public async Task<CheckoutStarted> Checkout(
        [Description("Plan key from list_plans, such as starter, pro, or business. change_plan and preview_plan_change also accept free for Personal workspaces.")] string plan,
        [Description("Billing interval: monthly or annual. Omit for free.")] string interval = "", string? organizationId = null, string? projectId = null)
    {
        if (PlanCatalog.For(plan).PriceCents <= 0)
            throw new AccountAccessException("Choose an available paid plan and a monthly or annual interval.", 400);
        var url = await billing.StartCheckoutAsync(Scope(organizationId, projectId), plan, interval, "/organization?tab=billing", Cancellation);
        return new(url);
    }

    [McpServerTool(Name = "get_billing_portal_url", Title = "Open billing portal", OpenWorld = true, Destructive = false, UseStructuredContent = true), Description("Create a billing portal session where the owner updates payment methods and downloads invoices. Returns the URL to open in a browser. Owner only.")]
    public async Task<BillingMcpPortal> Portal(string? organizationId = null, string? projectId = null) => new(await billing.PortalAsync(Scope(organizationId, projectId), Cancellation));

    [McpServerTool(Name = "list_notifications", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("Read notifications visible in your selected organization and project.")]
    public async Task<NotificationToolList> Notifications(string status = "", int page = 1, string? organizationId = null, string? projectId = null)
    {
        var context = await AccountScope.RefreshAsync(accounts, Scope(organizationId, projectId), Cancellation);
        var inbox = NotificationCommands.Inbox(context, status, page);
        return new(inbox.Items.Select(item => new NotificationToolItem(item.Id, item.Type, item.Title, item.Body,
            item.Unread ? "Unread" : "Read", item.Severity, item.CreatedAt)).ToArray(), inbox.Status, inbox.Unread, inbox.Critical,
            inbox.Total, inbox.Matching, inbox.Page, inbox.PageCount);
    }

    [McpServerTool(Name = "mark_notification_read", UseStructuredContent = true, Idempotent = true, Destructive = false, OpenWorld = false), Description("Mark one visible notification as read.")]
    public async Task<NotificationsMarked> ReadNotification(string notificationId, string? organizationId = null, string? projectId = null)
        => new(await NotificationCommands.MarkAsync(accounts, Scope(organizationId, projectId), notificationId, Cancellation));

    [McpServerTool(Name = "mark_all_notifications_read", UseStructuredContent = true, Idempotent = true, Destructive = false, OpenWorld = false), Description("Mark all notifications in the current scope as read.")]
    public async Task<NotificationsMarked> ReadAllNotifications(string? organizationId = null, string? projectId = null)
        => new(await NotificationCommands.MarkAsync(accounts, Scope(organizationId, projectId), null, Cancellation));

    private static string RequiredVersion(string version) => string.IsNullOrWhiteSpace(version) ? throw new AccountAccessException("A document version is required.", 422) : version;

    private DashboardMcpOrganizationDetails OrganizationDetails(AccountContext context)
    {
        var org = context.Organization!;
        return new(org.Id, org.Name, org.Kind, org.Status, org.RoleFor(context.UserId), org.PlanId,
            PlanCatalog.For(org)?.Name ?? "", org.BillingStatus,
            org.CanManage(context.UserId), org.CanProvision(context.UserId), org.DeletionRequested,
            org.Projects.Values.OrderBy(project => project.Name, StringComparer.Ordinal).Select(project =>
                new DashboardMcpProject(project.Id, project.Name, project.Region, project.BackupRegion, project.Regions.ToArray(), project.IsDefault, project.Databases.Count)).ToArray(),
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
        return new(org.Id, org.Name, org.Kind, org.Status, org.RoleFor(context.UserId), org.PlanId,
            PlanCatalog.For(org)?.Name ?? "", org.BillingStatus,
            org.CanManage(context.UserId), org.CanProvision(context.UserId), org.DeletionRequested,
            org.Projects.Values.OrderBy(project => project.Name, StringComparer.Ordinal).Select(project =>
                new DashboardMcpProject(project.Id, project.Name, project.Region, project.BackupRegion, project.Regions.ToArray(), project.IsDefault, project.Databases.Count)).ToArray());
    }
}
