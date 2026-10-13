
namespace TerraScale.Dashboard.Features.Organizations;

// Native forms and MCP calls share provider mutations and canonical CAS writes.
public sealed class OrganizationOperations(AccountService accounts, IWorkspaceGateway gateway,
    IWorkspacePlacement placement, IOrganizationAccess access, DashboardSessions sessions,
    TimeProvider time, ILogger<OrganizationOperations> logger)
{
    public async Task RenameAsync(AccountContext context, string name, CancellationToken cancellationToken)
    {
        context = await FreshOwnerAsync(context, cancellationToken);
        name = AccountService.ValidName(name);
        await gateway.RenameOrganizationAsync(context.Session, context.Organization!.Id, name, cancellationToken);
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            Owner(org, context.UserId);
            org.Name = name;
            org.Slug = AccountService.Slug(name);
            org.AuditEvents.Add(Event(context, "organization.renamed", name, "organization"));
        }, cancellationToken);
    }

    public async Task<ProjectRecord> CreateProjectAsync(AccountContext context, string name, string region,
        string backupRegion, IReadOnlyList<string>? regions, CancellationToken cancellationToken)
    {
        context = await FreshOwnerAsync(context, cancellationToken);
        name = AccountService.ValidName(name);
        CanCreate(context.Organization!, context.UserId, name);
        var locations = await LocationsAsync(region, backupRegion, regions, cancellationToken);
        var project = await gateway.CreateProjectAsync(context.Session, context.Organization!.Id, name, cancellationToken);
        if (project.Id.Length == 0 || project.OrganizationId != context.Organization.Id)
            throw new IOException("Project provisioning returned an invalid identity.");
        project.Region = locations.Primary;
        project.BackupRegion = locations.Backup;
        project.Regions = locations.Regions;
        await placement.RegisterAsync(context, project, cancellationToken);
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            Owner(org, context.UserId);
            CanCreate(org, context.UserId, name);
            if (org.Projects.ContainsKey(project.Id)) throw new AccountAccessException("Project identity already exists.", 409);
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
        context = await FreshOwnerAsync(context, cancellationToken, requireProject: true);
        name = AccountService.ValidName(name);
        var project = context.Project!;
        Duplicate(context.Organization!, name, project.Id);
        var locations = regions is { Count: > 0 }
            ? await LocationsAsync(region, backupRegion, regions, cancellationToken)
            : (Primary: project.Region, Backup: project.BackupRegion, Regions: project.Regions.ToList());
        await gateway.RenameProjectAsync(context.Session, context.Organization!.Id, project.Id, name, cancellationToken);
        var registration = new ProjectRecord
        {
            Id = project.Id,
            OrganizationId = project.OrganizationId,
            Name = name,
            Region = locations.Primary,
            BackupRegion = locations.Backup,
            Regions = locations.Regions
        };
        if (regions is { Count: > 0 }) await placement.RegisterAsync(context, registration, cancellationToken);
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            Owner(org, context.UserId);
            Duplicate(org, name, project.Id);
            var current = org.Projects.GetValueOrDefault(project.Id) ?? throw new AccountAccessException("Project is unavailable.", 404);
            if (current.OrganizationId != org.Id) throw new AccountAccessException("Project is unavailable.", 404);
            current.Name = name;
            current.Slug = AccountService.Slug(name);
            if (regions is { Count: > 0 })
            {
                current.Region = locations.Primary;
                current.BackupRegion = locations.Backup;
                current.Regions = locations.Regions;
            }
            org.AuditEvents.Add(Event(context, "project.renamed", name, "project"));
        }, cancellationToken);
    }

    public async Task MutateMemberAsync(AccountContext context, string operation, string userId, string role,
        CancellationToken cancellationToken)
    {
        context = operation is "confirm" or "cancel"
            ? await AccountScope.RefreshAsync(accounts, context, cancellationToken)
            : await FreshOwnerAsync(context, cancellationToken);
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
        // before writing the authoritative provider membership projection.
        context = await AccountScope.RefreshAsync(accounts, context, cancellationToken);
        var projection = await access.ReadAsync(context, cancellationToken);
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            org.Members = projection.Members.ToDictionary(member => member.UserId);
            org.OwnerUserId = projection.Members.FirstOrDefault(member => member.Role == "owner")?.UserId ?? org.OwnerUserId;
            org.PendingOwnerUserId = projection.PendingTransfer?.ToUserId ?? "";
            org.PendingOwnerExpiresAt = projection.PendingTransfer?.ExpiresAt;
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
            { logger.LogWarning(error, "Revoke dashboard sessions after committed member removal for {UserId}", userId); }
        }
    }

    public static void AvailableRole(string role)
    {
        if (role == "viewer") throw new AccountAccessException("The identity provider does not offer a viewer role. No membership was changed.", 422);
        if (role is not ("member" or "admin")) throw new AccountAccessException("Choose an available role.", 422);
    }

    public async Task<AccountContext> FreshOwnerAsync(AccountContext context, CancellationToken cancellationToken, bool requireProject = false)
    {
        context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject);
        Owner(context.Organization!, context.UserId);
        return context;
    }

    internal static void Owner(OrganizationRecord org, string userId)
    {
        if (org.OwnerUserId != userId || org.RoleFor(userId) != "owner") throw new AccountAccessException("Organization ownership is required.");
    }

    private static void CanCreate(OrganizationRecord org, string userId, string name)
    {
        if (!org.CanProvision(userId)) throw new AccountAccessException("Active billing is required to create projects.", 409);
        Duplicate(org, name);
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
        return (regions.Contains(primary) ? primary : regions[0], regions.Contains(backup) ? backup : regions[0], regions);
    }

    private AuditRecord Event(AccountContext context, string action, string target, string type) =>
        new(AccountService.NewId(), context.Account.Email, action, target, type, time.GetUtcNow());
}
