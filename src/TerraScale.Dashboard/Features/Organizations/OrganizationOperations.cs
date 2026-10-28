using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Features.Organizations;

// Native forms and MCP calls share provider mutations and canonical CAS writes.
public sealed class OrganizationOperations(AccountService accounts, ITsAuthPlatform platform,
    IWorkspacePlacement placement, IOrganizationAccess access, DashboardSessions sessions,
    TimeProvider time, ILogger<OrganizationOperations> logger)
{
    public async Task RenameAsync(AccountContext context, string name, CancellationToken cancellationToken)
    {
        context = await FreshManagerAsync(context, cancellationToken);
        name = AccountService.ValidName(name);
        await platform.RenameOrganizationAsync(context.Session, context.Organization!.Id, name, cancellationToken);
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            Manage(org, context.UserId);
            org.AuditEvents.Add(Event(context, "organization.renamed", name, "organization"));
        }, cancellationToken);
    }

    public async Task<ProjectRecord> CreateProjectAsync(AccountContext context, string name, string region,
        string backupRegion, IReadOnlyList<string>? regions, CancellationToken cancellationToken)
    {
        context = await FreshManagerAsync(context, cancellationToken);
        name = AccountService.ValidName(name);
        CanCreate(context.Organization!, context.UserId, name);
        var locations = await LocationsAsync(region, backupRegion, regions, cancellationToken);
        // ts-auth refuses a duplicate name (409 name_taken), so a repeated submit fails clearly.
        var remote = await platform.CreateProjectAsync(context.Session, context.Organization!.Id, name, cancellationToken);
        var project = new ProjectRecord
        {
            Id = remote.Id,
            OrganizationId = remote.OrganizationId,
            Name = remote.Name,
            Slug = AccountService.Slug(remote.Name),
            CreatedAt = remote.CreatedAt,
            Region = locations.Primary,
            BackupRegion = locations.Backup,
            Regions = locations.Regions
        };
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            // ts-auth already lists the new project; its dashboard settings start here.
            Manage(org, context.UserId);
            if (!org.CanProvision(context.UserId)) throw new AccountAccessException("Active billing is required to create projects.", 409);
            DemandProjectRoom(org, project.Id);
            Duplicate(org, name, project.Id);
            org.Projects[project.Id] = project;
            org.AuditEvents.Add(Event(context, "project.created", name, "project"));
        }, cancellationToken);
        // An explicit MCP scope never replaces an unrelated saved selection.
        await accounts.UpdateAccountAsync(context.Session, account =>
        {
            if (account.ActiveOrganizationId == project.OrganizationId && account.ActiveProjectId.Length == 0)
                account.ActiveProjectId = project.Id;
        }, cancellationToken);
        return project;
    }

    public async Task UpdateProjectAsync(AccountContext context, string name, string region, string backupRegion,
        IReadOnlyList<string>? regions, CancellationToken cancellationToken)
    {
        context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
        if (context.Project!.Role != "admin" || !context.CanWriteProjectData)
            throw new AccountAccessException("Project admin access is required to rename this project.");
        var placementRequested = region.Length > 0 || backupRegion.Length > 0 || regions is { Count: > 0 };
        if (!context.CanManage && placementRequested)
            throw new AccountAccessException("Organization management is required to change project placement. Submit only a project name.");
        name = AccountService.ValidName(name);
        var project = context.Project!;
        // Names belong to ts-auth. A project grant never authorizes an organization aggregate write.
        if (!placementRequested)
        {
            if (project.Name != name) await platform.RenameProjectAsync(context.Session, context.Organization!.Id, project.Id, name, cancellationToken);
            return;
        }
        Duplicate(context.Organization!, name, project.Id);
        // A primary or backup region alone moves within the project's current regions; it is never ignored.
        if (regions is not { Count: > 0 })
        {
            regions = project.Regions.Count > 0 ? project.Regions.ToList() : [project.Region];
            if (region.Length == 0) region = project.Region;
            if (backupRegion.Length == 0) backupRegion = project.BackupRegion;
        }
        var locations = await LocationsAsync(region, backupRegion, regions, cancellationToken);
        if (project.Name != name) await platform.RenameProjectAsync(context.Session, context.Organization!.Id, project.Id, name, cancellationToken);
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            Manage(org, context.UserId);
            Duplicate(org, name, project.Id);
            var current = org.Projects.GetValueOrDefault(project.Id) ?? throw new AccountAccessException("Project is unavailable.", 404);
            if (current.OrganizationId != org.Id) throw new AccountAccessException("Project is unavailable.", 404);
            current.Name = name;
            current.Slug = AccountService.Slug(name);
            current.Region = locations.Primary;
            current.BackupRegion = locations.Backup;
            current.Regions = locations.Regions;
            org.AuditEvents.Add(Event(context, "project.renamed", name, "project"));
        }, cancellationToken);
    }

    public async Task MutateMemberAsync(AccountContext context, string operation, string userId, string role,
        CancellationToken cancellationToken)
    {
        context = operation is "confirm" or "cancel"
            ? await AccountScope.RefreshAsync(accounts, context, cancellationToken)
            : operation == "start" ? await FreshOwnerAsync(context, cancellationToken)
            : await FreshManagerAsync(context, cancellationToken);
        if (operation is "role" or "remove" or "start")
        {
            var snapshot = await access.ReadAsync(context, cancellationToken);
            var member = snapshot.Members.SingleOrDefault(item => item.UserId == userId);
            if (member is null || member.Status != "active" || userId == context.UserId || member.Role == "owner")
                throw new AccountAccessException("Choose another active member than yourself or the owner.", 422);
        }
        if (operation == "role")
        {
            AvailableRole(role);
            await access.ChangeRoleAsync(context, userId, role, cancellationToken);
        }
        else if (operation == "remove") await access.RemoveMemberAsync(context, userId, cancellationToken);
        else await access.TransferAsync(context, operation, userId, cancellationToken);
        // Ownership confirmation changes the caller's role. Resolve it again
        // before recording the change; the write refreshes members from ts-auth.
        context = await AccountScope.RefreshAsync(accounts, context, cancellationToken);
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            org.AuditEvents.Add(Event(context, operation switch
            {
                "role" => "member.role_changed",
                "remove" => "member.removed",
                "start" => "ownership.requested",
                "confirm" => "ownership.transferred",
                _ => "ownership.canceled"
            }, userId, "member"));
        }, cancellationToken);
        if (operation == "remove")
        {
            try { await sessions.RevokeUserAsync(userId, cancellationToken); }
            catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException)
            { logger.LogWarning("Revoke dashboard sessions after committed member removal for {UserId} ({ErrorType})", userId, error.GetType().Name); }
        }
    }

    public static void AvailableRole(string role)
    {
        if (role is not ("viewer" or "member" or "admin")) throw new AccountAccessException("Choose an available role.", 422);
    }

    public async Task<AccountContext> FreshOwnerAsync(AccountContext context, CancellationToken cancellationToken, bool requireProject = false)
    {
        context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject);
        Owner(context.Organization!, context.UserId);
        return context;
    }

    private async Task<AccountContext> FreshManagerAsync(AccountContext context, CancellationToken cancellationToken, bool requireProject = false)
    {
        context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject);
        Manage(context.Organization!, context.UserId);
        return context;
    }

    internal static void Manage(OrganizationRecord org, string userId)
    {
        if (!org.CanManage(userId)) throw new AccountAccessException("Organization management is required.");
    }

    internal static void Owner(OrganizationRecord org, string userId)
    {
        if (org.OwnerUserId != userId || org.RoleFor(userId) != "owner") throw new AccountAccessException("Organization ownership is required.");
    }

    private static void CanCreate(OrganizationRecord org, string userId, string name)
    {
        if (!org.CanProvision(userId)) throw new AccountAccessException("Active billing is required to create projects.", 409);
        DemandProjectRoom(org);
        Duplicate(org, name);
    }

    // Mirrors DatabaseService.DatabaseLimit: an internal policy without a project limit is unlimited.
    public static int ProjectLimit(OrganizationRecord org) => org.InternalBilling is not null
        ? PlanCatalog.ResourceLimits(org).Projects is > 0 and var limit ? limit : int.MaxValue
        : PlanCatalog.Plans.Any(plan => plan.Id == org.PlanId) ? PlanCatalog.ResourceLimits(org.PlanId).Projects
        : throw new AccountAccessException("The organization plan is unavailable.", 503);

    internal static void DemandProjectRoom(OrganizationRecord org, string? createdProjectId = null)
    {
        var limit = ProjectLimit(org);
        // The canonical provider snapshot already includes this request's new project.
        if (org.Projects.Keys.Count(id => id != createdProjectId) >= limit)
            throw new AccountAccessException($"Your organization has reached its plan's project limit ({limit} {(limit == 1 ? "project" : "projects")}). Upgrade the plan on the organization's Billing tab or delete a project.", 409);
    }

    private static void Duplicate(OrganizationRecord org, string name, string? except = null)
    {
        var slug = AccountService.Slug(name);
        if (org.Projects.Values.Any(project => project.Id != except && project.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase)))
            throw new AccountAccessException("A project with that name already exists.", 409);
    }

    private async Task<(string Primary, string Backup, List<string> Regions)> LocationsAsync(string primary, string backup,
        IReadOnlyList<string>? requested, CancellationToken cancellationToken)
    {
        var catalog = await placement.RegionsAsync(cancellationToken);
        if (catalog.Count == 0) throw new IOException("Serving regions could not be discovered.");
        var regions = requested is { Count: > 0 } ? requested.Select(value => value.Trim().ToUpperInvariant()).Distinct().ToList()
            : new List<string> { string.IsNullOrWhiteSpace(primary) ? catalog[0] : primary.Trim().ToUpperInvariant() };
        if (regions.Any(region => !catalog.Contains(region))) throw new AccountAccessException("Choose serving project regions.", 422);
        primary = primary.Trim().ToUpperInvariant();
        backup = backup.Trim().ToUpperInvariant();
        if (primary.Length > 0 && !regions.Contains(primary)) throw new AccountAccessException("Choose a primary region from the project's regions.", 422);
        if (backup.Length > 0 && !regions.Contains(backup)) throw new AccountAccessException("Choose a backup region from the project's regions.", 422);
        return (regions.Contains(primary) ? primary : regions[0], regions.Contains(backup) ? backup : regions[0], regions);
    }

    private AuditRecord Event(AccountContext context, string action, string target, string type) =>
        new(AccountService.NewId(), context.Account.Email, action, target, type, time.GetUtcNow());
}
