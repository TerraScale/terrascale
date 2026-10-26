using System.ComponentModel;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Organizations.Invitations;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Organizations;

[McpServerToolType]
public sealed partial class OrganizationMcpTools(IHttpContextAccessor http, AccountService accounts, OrganizationOperations operations,
    OrganizationLifecycleService lifecycle, InvitationService invitations, IOrganizationAccess access, TimeProvider time)
{
    private const string OrganizationDescription = "Organization ID to act on. Defaults to the caller's current dashboard organization.";
    private const string ProjectDescription = "Project ID to act on. Defaults to the current project, or the organization's default project when organizationId is given.";

    [McpServerTool(Name = "rename_organization", Title = "Rename organization", Idempotent = true, Destructive = false, OpenWorld = true, UseStructuredContent = true), Description("Rename an organization. Requires the owner or admin role.")]
    public Task<OrganizationDone> RenameOrganization([Description("New name: 1-128 characters including a letter or digit.")] string name,
        [Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null) => Invoke(async () =>
    { await operations.RenameAsync(Scope(organizationId, projectId), name, Cancellation); return new OrganizationDone(true); });

    [McpServerTool(Name = "create_project", Title = "Create project", Destructive = false, OpenWorld = true, UseStructuredContent = true), Description("Create a project in an organization. Requires the owner or admin role and, for business organizations, active billing.")]
    public Task<OrganizationProjectResult> CreateProject([Description("Project name: 1-128 characters including a letter or digit.")] string name,
        [Description("Primary region code TerraBase serves, such as GRU. Defaults to the first listed region.")] string region = "",
        [Description("Backup region code. Defaults to the first listed region.")] string backupRegion = "",
        [Description("Region codes the project may use. Defaults to the primary region.")] string[]? regions = null,
        [Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null) => Invoke(async () =>
    {
        var project = await operations.CreateProjectAsync(Scope(organizationId, projectId), name, region, backupRegion, regions, Cancellation);
        return new OrganizationProjectResult(project.Id, project.OrganizationId, project.Name, project.Region, project.BackupRegion, project.Regions);
    });

    [McpServerTool(Name = "update_project", Title = "Update project", Idempotent = true, Destructive = false, OpenWorld = true, UseStructuredContent = true), Description("Rename the project projectId selects and optionally change its regions. Requires the owner or admin role.")]
    public Task<OrganizationDone> UpdateProject([Description("Project name: 1-128 characters including a letter or digit.")] string name,
        [Description("Primary region code TerraBase serves, such as GRU. Defaults to the first listed region.")] string region = "",
        [Description("Backup region code. Defaults to the first listed region.")] string backupRegion = "",
        [Description("Region codes the project may use. Defaults to the primary region.")] string[]? regions = null,
        [Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null) => Invoke(async () =>
    { await operations.UpdateProjectAsync(Scope(organizationId, projectId), name, region, backupRegion, regions, Cancellation); return new OrganizationDone(true); });

    [McpServerTool(Name = "request_organization_deletion", Title = "Request organization deletion", Idempotent = true, Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Request deletion of a business organization. Requires the owner role and confirmName equal to the organization name.")]
    public Task<OrganizationDone> RequestDeletion([Description("The organization's exact name, confirming the deletion request.")] string confirmName,
        [Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null) => Invoke(async () =>
    { await lifecycle.DeleteAsync(Scope(organizationId, projectId), confirmName, Cancellation); return new OrganizationDone(true); });

    [McpServerTool(Name = "leave_organization", Title = "Leave organization", Destructive = true, OpenWorld = true, UseStructuredContent = true), Description("Leave a business organization the caller does not own. The dashboard selection moves to another organization.")]
    public Task<OrganizationDone> LeaveOrganization([Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null) => Invoke(async () =>
    { await lifecycle.LeaveAsync(Scope(organizationId, projectId), Cancellation); return new OrganizationDone(true); });

    [McpServerTool(Name = "update_member_role", Title = "Update member role", Idempotent = true, Destructive = false, OpenWorld = true, UseStructuredContent = true), Description("Change a member's role to viewer, member or admin. Requires the owner or admin role.")]
    public Task<OrganizationDone> UpdateMemberRole([Description("Member user ID from list_members.")] string userId,
        [Description("viewer (read only), member (read and write data) or admin (manages members, projects and keys).")] string role,
        [Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null) => Invoke(async () =>
    {
        if (role is not ("viewer" or "member" or "admin")) throw new AccountAccessException("Role must be viewer, member or admin.", 400);
        await operations.MutateMemberAsync(Scope(organizationId, projectId), "role", userId, role, Cancellation);
        return new OrganizationDone(true);
    });

    [McpServerTool(Name = "remove_member", Title = "Remove member", Destructive = true, OpenWorld = true, UseStructuredContent = true), Description("Remove a member's user access to an organization and its projects and revoke their dashboard sessions. Organization and project service keys remain active; rotate or revoke any keys known to them at /api-keys. Requires the owner or admin role.")]
    public Task<OrganizationDone> RemoveMember([Description("Member user ID from list_members.")] string userId,
        [Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null) => Member("remove", userId, organizationId, projectId);

    [McpServerTool(Name = "request_ownership_transfer", Title = "Request ownership transfer", Idempotent = true, Destructive = false, OpenWorld = true, UseStructuredContent = true), Description("Nominate an active member as the next owner. The member must confirm with confirm_ownership_transfer within the provider's transfer expiry. Requires the owner role.")]
    public Task<OrganizationDone> RequestOwnershipTransfer([Description("Member user ID from list_members.")] string userId,
        [Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null) => Member("start", userId, organizationId, projectId);

    [McpServerTool(Name = "confirm_ownership_transfer", Title = "Confirm ownership transfer", Destructive = true, OpenWorld = true, UseStructuredContent = true), Description("Accept a pending ownership transfer nominated to the caller. The previous owner becomes an admin.")]
    public Task<OrganizationDone> ConfirmOwnershipTransfer([Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null) => Member("confirm", "", organizationId, projectId);

    [McpServerTool(Name = "list_members", Title = "List members", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true, UseStructuredContent = true), Description("List an organization's active members with their roles, optionally filtered by name, email, or role.")]
    public Task<OrganizationMemberList> ListMembers([Description("Case-insensitive filter on member name or email.")] string search = "",
        [Description("Only members with this role: owner, admin, member, or viewer.")] string role = "",
        [Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null,
        [Description(CursorPaging.Description)] int limit = 100, string after = "") => Invoke(async () =>
    {
        if (role is not ("" or "all" or "owner" or "admin" or "member" or "viewer")) throw new AccountAccessException("Role must be owner, admin, member, or viewer.", 400);
        var context = await FreshAsync(organizationId, projectId);
        var members = (await access.ReadAsync(context, Cancellation)).Members.Where(member => member.Status == "active").ToArray();
        var matching = members.Where(member => (role is "" or "all" || member.Role == role) &&
            (search.Trim().Length == 0 || member.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) || member.Email.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OrderBy(member => member.Name).ThenBy(member => member.UserId)
            .Select(member => new OrganizationMemberView(member.UserId, member.Name, member.Email, member.Role,
                member.UserId == context.Organization!.OwnerUserId || member.Role == "owner", member.UserId == context.UserId,
                context.CanManage && member.UserId != context.UserId && member.Role != "owner",
                member.CreatedAt != DateTimeOffset.MinValue ? member.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd") : member.JoinedOn?.ToString("yyyy-MM-dd") ?? "")).ToArray();
        var page = CursorPaging.Page(matching, member => member.Id, CursorPaging.Scope(context, "members", search.Trim(), role), limit, after);
        return new OrganizationMemberList(page.Items, new(members.Count(member => member.Role == "owner"), members.Count(member => member.Role == "member"),
            members.Count(member => member.Role == "viewer"), members.Count(member => member.Role == "admin")), page.NextCursor, page.HasMore, page.Limit);
    });

    [McpServerTool(Name = "list_invitations", Title = "List invitations", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true, UseStructuredContent = true), Description("List an organization's invitations with their status and expiry, ordered by immutable ID.")]
    public Task<OrganizationInvitationList> ListInvitations([Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null,
        [Description(CursorPaging.Description)] int limit = 100, string after = "") => Invoke(async () =>
    {
        var context = await FreshAsync(organizationId, projectId);
        var snapshot = await access.ReadAsync(context, Cancellation);
        var rows = snapshot.Invitations.Select(invite => View(invite.Id, invite.Email, invite.Role, invite.Status, invite.ExpiresAt));
        var page = CursorPaging.Page(rows, invite => invite.Id, CursorPaging.Scope(context, "invitations"), limit, after);
        return new OrganizationInvitationList(page.Items, page.NextCursor, page.HasMore, page.Limit);
    });

    [McpServerTool(Name = "invite_member", Title = "Invite member", Destructive = false, OpenWorld = true, UseStructuredContent = true), Description("Invite an email address to an organization and return a one-time invitation link. Requires the owner or admin role and plan member capacity.")]
    public Task<OrganizationIssuedInvitation> InviteMember([Description("Email address to invite.")] string email,
        [Description("viewer (read only), member (read and write data) or admin (manages members, projects and keys). Defaults to member.")] string role = "member",
        [Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null) => Invoke(async () =>
    {
        if (role is not ("" or "viewer" or "member" or "admin")) throw new AccountAccessException("Role must be viewer, member or admin.", 400);
        var created = await invitations.CreateAsync(Scope(organizationId, projectId), email, role, Cancellation);
        return Issued(created);
    });

    [McpServerTool(Name = "resend_invitation", Title = "Resend invitation", Destructive = true, OpenWorld = true, UseStructuredContent = true), Description("Replace a pending invitation's link. The previous link stops working. Requires the owner or admin role; the provider supplies the new expiry and invitation ID.")]
    public Task<OrganizationIssuedInvitation> ResendInvitation([Description("Invitation ID from list_invitations.")] string invitationId,
        [Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null) => Invoke(async () =>
        Issued(await invitations.ResendAsync(Scope(organizationId, projectId), invitationId, Cancellation)));

    [McpServerTool(Name = "revoke_invitation", Title = "Revoke invitation", Destructive = true, OpenWorld = true, UseStructuredContent = true), Description("Cancel a pending invitation so its link no longer works. Requires the owner or admin role.")]
    public Task<OrganizationDone> RevokeInvitation([Description("Invitation ID from list_invitations.")] string invitationId,
        [Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null) => Invoke(async () =>
    { await invitations.RevokeAsync(Scope(organizationId, projectId), invitationId, Cancellation); return new OrganizationDone(true); });

    [McpServerTool(Name = "get_invitation", Title = "Get invitation", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Show the organization, role, and expiry of a pending invitation from its token or link, and whether the caller can respond.")]
    public Task<OrganizationInvitationPreview> GetInvitation([Description("Invitation token or the full invitation link.")] string token) => Invoke(async () =>
    {
        var context = await PrincipalAsync();
        var pending = await invitations.FindAsync(context.Session, Token(token), Cancellation) ?? throw new AccountAccessException("No pending invitation matches that token.", 404);
        return new OrganizationInvitationPreview(pending.Organization.Id, pending.Organization.Name, pending.Invitation.Email, pending.Invitation.Role,
            pending.Invitation.ExpiresAt, pending.Invitation.Email.Equals(context.Account.Email, StringComparison.OrdinalIgnoreCase));
    });

    [McpServerTool(Name = "respond_to_invitation", Title = "Respond to invitation", Destructive = false, OpenWorld = true, UseStructuredContent = true), Description("Accept or decline an invitation addressed to the caller's email. Accepting selects the invited organization or project.")]
    public Task<OrganizationInvitationResponse> RespondToInvitation([Description("Invitation token or the full invitation link.")] string token,
        [Description("accept or decline.")] string action) => Invoke(async () =>
    {
        var context = await PrincipalAsync();
        var organization = (await invitations.RespondAsync(context.Session, Token(token), action, Cancellation)).Organization;
        return new OrganizationInvitationResponse(organization.Id, organization.Name, action.Trim().ToLowerInvariant());
    });

    [McpServerTool(Name = "list_audit_events", Title = "List audit events", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true, UseStructuredContent = true), Description("List the audit history of a project, newest first, with the same filters and pagination as the audit page and export.")]
    public Task<OrganizationAuditEvents> ListAuditEvents(
        [Description("Case-insensitive text matched against actor, action, target, source, and severity.")] string search = "",
        [Description("Only events with this raw action code. Omit or use all for every action.")] string action = "",
        [Description("info, warn, critical, unknown (not reported by the provider), or all (default).")] string severity = "",
        [Description("Time window: 24h, 7d (default), 30d, 90d, or 365d, capped by the plan's audit retention (retentionDays).")] string range = "",
        [Description("1-based page number. Defaults to 1.")] int page = 1,
        [Description("Events per page, 1-100. Defaults to 10.")] int pageSize = 10,
        [Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null) => Invoke(async () =>
    {
        if (pageSize == 0) pageSize = 10;
        if (pageSize is < 1 or > 100) throw new AccountAccessException("pageSize must be between 1 and 100.", 400);
        var context = await FreshAsync(organizationId, projectId);
        if (!context.CanManage) throw new AccountAccessException("Organization management is required.");
        var snapshot = await access.ReadAsync(context, Cancellation);
        var retention = snapshot.AuditRetentionDays is >= 1 ? snapshot.AuditRetentionDays.Value
            : throw new IOException("Audit retention policy is unavailable.");
        range = range is "24h" or "30d" or "90d" or "365d" ? range : "7d";
        severity = severity is "info" or "warn" or "critical" or "unknown" ? severity : "all";
        var filter = new OrganizationFilters(Search: search.Trim(), Action: action.Length == 0 ? "all" : action, Severity: severity, Range: range, Now: time.GetUtcNow());
        var matches = filter.ApplyAudit(snapshot.AuditEvents, retention);
        var pages = Math.Max(1, (matches.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 1, pages);
        var retained = snapshot.AuditEvents.Where(item => item.OccurredAt == DateTimeOffset.MinValue || item.OccurredAt >= time.GetUtcNow().AddDays(-retention));
        var rows = matches.Skip((page - 1) * pageSize).Take(pageSize).Select(item => new OrganizationAuditEventView(item.Id, item.OccurredAt,
            item.Actor, OrganizationFilters.AuditActorType(item), item.Action, OrganizationFilters.AuditActionLabel(item), item.Target,
            item.TargetType, item.Source, OrganizationFilters.AuditSeverity(item), OrganizationFilters.AuditOutcome(item), item.Ip ?? "")).ToArray();
        return new OrganizationAuditEvents(rows, matches.Count, matches.Count(item => OrganizationFilters.AuditSeverity(item) == "critical"),
            matches.Count(item => OrganizationFilters.AuditSeverity(item) == "warn"), matches.Count(item => OrganizationFilters.AuditSeverity(item) == "info"),
            matches.Count(item => OrganizationFilters.AuditSeverity(item) == "unknown"),
            page, pages, range, severity, retained.Select(item => item.Action).Distinct().Order().ToArray(), retention);
    });

    private Task<OrganizationDone> Member(string operation, string userId, string? organizationId, string? projectId) => Invoke(async () =>
    { await operations.MutateMemberAsync(Scope(organizationId, projectId), operation, userId, "", Cancellation); return new OrganizationDone(true); });
    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private Task<AccountContext> FreshAsync(string? organizationId, string? projectId) => AccountScope.RefreshAsync(accounts, Scope(organizationId, projectId), Cancellation);
    private Task<AccountContext> PrincipalAsync() => accounts.GetContextAsync(
        (http.HttpContext?.Items[DashboardMcp.ContextKey] as AccountContext ?? throw new AccountAccessException("Authentication is required.", 401)).Session, Cancellation);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
    private static string Token(string raw)
    {
        raw = raw.Trim();
        var marker = raw.IndexOf("/invitations/", StringComparison.Ordinal);
        return marker < 0 ? raw : Uri.UnescapeDataString(raw[(marker + 13)..].Split('/', '?', '#')[0]);
    }
    private OrganizationIssuedInvitation Issued(CreatedInvitation created) => new(
        View(created.Invitation.Id, created.Invitation.Email, created.Invitation.Role, "pending", created.Invitation.ExpiresAt),
        "/invitations/" + Uri.EscapeDataString(created.Token));
    private OrganizationInvitationView View(string id, string email, string role, string status, DateTimeOffset expires) => new(id, email, role,
        status == "pending" && expires != DateTimeOffset.MinValue && expires <= time.GetUtcNow() ? "Expired" : status switch
        { "pending" => "Pending", "expired" => "Expired", "accepted" => "Accepted", "declined" => "Declined", "revoked" or "canceled" => "Canceled", _ => status },
        expires == DateTimeOffset.MinValue ? "" : expires.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"), status == "pending");
    private async Task<T> Invoke<T>(Func<Task<T>> action)
    {
        // ts-auth refusals arrive as AccountAccessException with its status and message.
        try { return await action(); }
        catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !Cancellation.IsCancellationRequested)
        { throw new AccountAccessException("The organization provider could not complete this request. Reload it and try again.", 502); }
    }
}
