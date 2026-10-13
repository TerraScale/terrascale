using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Organizations;

public interface IOrganizationLifecycle
{
    Task LeaveAsync(AccountContext context, CancellationToken cancellationToken);
    Task DeleteAsync(AccountContext context, CancellationToken cancellationToken);
    Task DeleteProjectAsync(AccountContext context, string projectId, CancellationToken cancellationToken);
}

public sealed class TsOrganizationLifecycle(IHttpClientFactory http) : IOrganizationLifecycle
{
    private TerraBaseManagementClient Client(AccountContext context)
    {
        var client = http.CreateClient("platform");
        return new(client, new() { EndpointResolver = new TerraBaseDiscovery(), AccessTokenProvider = _ => ValueTask.FromResult(context.Session.AccessToken) });
    }
    public Task LeaveAsync(AccountContext context, CancellationToken cancellationToken) => Client(context).LeaveOrganizationAsync(context.UserId, context.Organization!.Id, cancellationToken);
    public Task DeleteAsync(AccountContext context, CancellationToken cancellationToken) => Client(context).DeleteOrganizationAsync(context.UserId, context.Organization!.Id, cancellationToken);
    public Task DeleteProjectAsync(AccountContext context, string projectId, CancellationToken cancellationToken) => Client(context).DeleteProjectAsync(context.UserId, context.Organization!.Id, projectId, cancellationToken);
}

public sealed class OrganizationLifecycleService(IDashboardState state, AccountService accounts, IOrganizationLifecycle gateway, TimeProvider time)
{
    public async Task LeaveAsync(AccountContext context, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, cancellationToken);
        var org = context.Organization!;
        if (org.Kind == "personal" || context.Role == "owner") throw new AccountAccessException("Owners must transfer ownership before leaving a business organization.", 409);
        if (context.Organizations.Count < 2) throw new AccountAccessException("Keep another accessible organization before leaving.", 409);
        await gateway.LeaveAsync(context, cancellationToken);
        await ProjectAfterRemovalAsync(context, false, cancellationToken);
        await ForgetAsync(context, cancellationToken);
    }

    public async Task RequestDeletionAsync(AccountContext context, string confirmation, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, cancellationToken);
        var org = context.Organization!;
        if (org.Kind == "personal" || context.Role != "owner") throw new AccountAccessException("Only the owner can delete a business organization.");
        if (confirmation.Trim() != org.Name) throw new AccountAccessException("Type the organization name to confirm deletion.", 422);
        await accounts.UpdateOrganizationAsync(context, current =>
        {
            if (current.Kind == "personal" || current.RoleFor(context.UserId) != "owner" || current.OwnerUserId != context.UserId)
                throw new AccountAccessException("Only the owner can request deletion of a business organization.");
            if (confirmation.Trim() != current.Name) throw new AccountAccessException("Type the organization name to confirm deletion.", 422);
            if (current.DeletionRequested) return;
            current.DeletionRequested = true;
            current.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "organization.deletion_requested", current.Name, "organization", time.GetUtcNow()));
        }, cancellationToken);
    }

    public async Task DeleteProjectAsync(AccountContext context, string projectId, string confirmation, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, cancellationToken);
        if (!context.CanManage) throw new AccountAccessException("Organization management is required.");
        var project = context.Organization!.Projects.GetValueOrDefault(projectId) ?? throw new AccountAccessException("Project is unavailable.", 404);
        if (project.OrganizationId != context.Organization.Id) throw new AccountAccessException("Project is unavailable.", 404);
        if (project.Name != confirmation.Trim()) throw new AccountAccessException("Type the project name to confirm deletion.", 422);
        if (project.IsDefault || context.Organization.Projects.Count <= 1) throw new AccountAccessException("The default or last project cannot be deleted.", 409);
        if (project.Databases.Count > 0) throw new AccountAccessException("Delete this project's databases before deleting the project.", 409);
        try { await gateway.DeleteProjectAsync(context, projectId, cancellationToken); }
        // A completed provider delete can outlive an interrupted canonical write.
        // The exact owned project and current role were authorized above; the CAS
        // projection below repeats those checks before reconciling its absence.
        catch (TerraBaseNotFoundException) { }
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            if (!org.CanManage(context.UserId)) throw new AccountAccessException("Organization management is required.");
            var current = org.Projects.GetValueOrDefault(projectId) ?? throw new AccountAccessException("Project is unavailable.", 404);
            if (current.IsDefault || org.Projects.Count <= 1) throw new AccountAccessException("The default or last project cannot be deleted.", 409);
            if (current.Name != confirmation.Trim() || current.Databases.Count > 0) throw new AccountAccessException("Project changed during deletion.", 409);
            org.Projects.Remove(projectId);
            org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "project.deleted", project.Name, "project", time.GetUtcNow()));
        }, cancellationToken);
        await accounts.UpdateAccountAsync(context.Session, account =>
        {
            if (account.ActiveProjectId == projectId)
                account.ActiveProjectId = context.Organization.Projects.Values.FirstOrDefault(item => item.Id != projectId && item.IsDefault)?.Id ?? context.Organization.Projects.Keys.Where(id => id != projectId).Order().FirstOrDefault() ?? "";
        }, cancellationToken);
    }

    private async Task<AccountContext> FreshAsync(AccountContext context, CancellationToken cancellationToken)
    {
        return await AccountScope.RefreshAsync(accounts, context, cancellationToken);
    }

    private Task ForgetAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var fallback = context.Organizations.Where(org => org.Id != context.Organization!.Id).OrderBy(org => org.Kind != "personal").ThenBy(org => org.Name).FirstOrDefault();
        return accounts.UpdateAccountAsync(context.Session, account =>
        {
            account.OrganizationIds.Remove(context.Organization!.Id);
            if (account.ActiveOrganizationId != context.Organization.Id) return;
            account.ActiveOrganizationId = fallback?.Id ?? "";
            account.ActiveProjectId = fallback?.Projects.Values.FirstOrDefault(project => project.IsDefault)?.Id ?? fallback?.Projects.Keys.Order().FirstOrDefault() ?? "";
        }, cancellationToken);
    }

    // The remote mutation has removed the actor's membership already. This
    // narrow projection cannot require the membership it has just deleted.
    private async Task ProjectAfterRemovalAsync(AccountContext context, bool deleted, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var document = await state.GetAsync(AccountService.OrganizationsCollection, context.Organization!.Id, cancellationToken);
            if (document is null) return;
            var org = AccountService.Read<OrganizationRecord>(document);
            if (org.Id != context.Organization.Id) throw new IOException("Organization projection identity changed.");
            if (deleted) { org.Archived = true; org.Status = "deleted"; org.DeletionRequested = true; }
            else org.Members.Remove(context.UserId);
            org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, deleted ? "organization.deleted" : "member.left", deleted ? org.Name : context.UserId, "organization", time.GetUtcNow()));
            try
            {
                await state.PutAsync(AccountService.OrganizationsCollection, org.Id, JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Matches(document.Version), cancellationToken);
                return;
            }
            catch (TerraBasePreconditionFailedException) when (attempt < 3) { }
        }
    }
}
