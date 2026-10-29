using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Organizations;

public sealed class OrganizationLifecycleService(IDashboardState state, AccountService accounts, ITsAuthPlatform platform, StripeBilling billing, TimeProvider time)
{
    public async Task LeaveAsync(AccountContext context, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, cancellationToken);
        var org = context.Organization!;
        if (org.Kind == "personal" || context.Role == "owner") throw new AccountAccessException("Owners must transfer ownership before leaving a business organization.", 409);
        if (context.Organizations.Count < 2) throw new AccountAccessException("Keep another accessible organization before leaving.", 409);
        await platform.LeaveOrganizationAsync(context.Session, org.Id, cancellationToken);
        await ProjectAfterRemovalAsync(context, false, cancellationToken);
        await ForgetAsync(context, cancellationToken);
    }

    // Completes on the owner's confirmed request. An organization already marked by the earlier
    // deletion request reaches the same path, so its owner can finish it.
    public async Task DeleteAsync(AccountContext context, string confirmation, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, cancellationToken);
        var org = context.Organization!;
        if (org.Kind == "personal" || context.Role != "owner") throw new AccountAccessException("Only the owner can delete a business organization.");
        if (confirmation.Trim() != org.Name) throw new AccountAccessException("Type the organization name to confirm deletion.", 422);
        // The mark stops new projects and billing changes while the deletion runs.
        await accounts.UpdateOrganizationAsync(context, current =>
        {
            if (current.Kind == "personal" || current.RoleFor(context.UserId) != "owner" || current.OwnerUserId != context.UserId)
                throw new AccountAccessException("Only the owner can delete a business organization.");
            if (confirmation.Trim() != current.Name) throw new AccountAccessException("Type the organization name to confirm deletion.", 422);
            if (current.Projects.Values.Any(project => project.Databases.Count > 0)) throw new AccountAccessException("Delete this organization's databases before deleting it.", 409);
            if (current.DeletionRequested) return;
            current.DeletionRequested = true;
            current.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "organization.deletion_requested", current.Name, "organization", time.GetUtcNow()));
        }, cancellationToken);
        // The fresh owner, exact name and persisted deletion mark authorize
        // recovery even when an interrupted Checkout never published its ID.
        await billing.EndSubscriptionAsync(org.Id, cancellationToken, authorizedDeletionPreflight: true);
        // ts-auth refuses an organization that still has projects; a 404 is a delete an interrupted request already made.
        foreach (var projectId in org.Projects.Keys)
        {
            try
            {
                if (!await platform.DeleteProjectAsync(context.Session, org.Id, projectId, cancellationToken))
                    throw PendingDeletion();
            }
            catch (AccountAccessException error) when (error.StatusCode == StatusCodes.Status404NotFound) { }
        }
        try { await platform.DeleteOrganizationAsync(context.Session, org.Id, cancellationToken); }
        catch (AccountAccessException error) when (error.StatusCode == StatusCodes.Status404NotFound) { }
        await ProjectAfterRemovalAsync(context, true, cancellationToken);
        await ForgetAsync(context, cancellationToken);
    }

    public async Task DeleteProjectAsync(AccountContext context, string projectId, string confirmation, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, cancellationToken);
        if (!context.CanManage) throw new AccountAccessException("Organization management is required.");
        var project = context.Organization!.Projects.GetValueOrDefault(projectId);
        var removed = project is null;
        if (removed && await state.GetAsync(AccountService.OrganizationsCollection, context.Organization.Id, cancellationToken) is { } saved)
        {
            var pendingName = AccountService.Read<OrganizationRecord>(saved).PendingProjectDeletions.GetValueOrDefault(projectId);
            if (!string.IsNullOrEmpty(pendingName) && pendingName == confirmation.Trim())
                project = new() { Id = projectId, Name = pendingName, OrganizationId = context.Organization.Id };
        }
        if (project is null) throw new AccountAccessException("Project is unavailable.", 404);
        if (project.OrganizationId != context.Organization.Id) throw new AccountAccessException("Project is unavailable.", 404);
        if (project.Name != confirmation.Trim()) throw new AccountAccessException("Type the project name to confirm deletion.", 422);
        if (!removed && (project.IsDefault || context.Organization.Projects.Count <= 1)) throw new AccountAccessException("The default or last project cannot be deleted.", 409);
        if (project.Databases.Count > 0) throw new AccountAccessException("Delete this project's databases before deleting the project.", 409);
        try
        {
            if (!removed)
                await accounts.UpdateOrganizationAsync(context, org =>
                {
                    if (!org.CanManage(context.UserId)) throw new AccountAccessException("Organization management is required.");
                    org.PendingProjectDeletions[projectId] = confirmation.Trim();
                }, cancellationToken);
            if (!removed && !await platform.DeleteProjectAsync(context.Session, context.Organization.Id, projectId, cancellationToken))
                throw PendingDeletion();
        }
        // A completed provider delete can outlive an interrupted dashboard write;
        // the exact project and current role were authorized above.
        catch (AccountAccessException error) when (error.StatusCode == StatusCodes.Status404NotFound) { }
        // ts-auth no longer lists the project, so this write drops its dashboard settings.
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            if (!org.CanManage(context.UserId)) throw new AccountAccessException("Organization management is required.");
            org.Projects.Remove(projectId);
            org.PendingProjectDeletions.Remove(projectId);
            org.DeletedProjectIds.Add(projectId);
            org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "project.deleted", project.Name, "project", time.GetUtcNow()));
        }, cancellationToken);
        await accounts.UpdateAccountAsync(context.Session, account =>
        {
            if (account.ActiveProjectId == projectId)
                account.ActiveProjectId = context.Organization.Projects.Values.FirstOrDefault(item => item.Id != projectId && item.IsDefault)?.Id ?? context.Organization.Projects.Keys.Where(id => id != projectId).Order().FirstOrDefault() ?? "";
        }, cancellationToken);
    }

    private static AccountAccessException PendingDeletion() => new("Project deletion is still in progress. Try again shortly; the organization will stay until its project closures are confirmed.", 409, "deletion_pending");

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
            org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, deleted ? "organization.deleted" : "member.left", deleted ? org.Name : context.UserId, "organization", time.GetUtcNow(), Ip: accounts.ClientIp));
            try
            {
                await state.PutAsync(AccountService.OrganizationsCollection, org.Id, JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Matches(document.Version), cancellationToken);
                return;
            }
            catch (TerraBasePreconditionFailedException) when (attempt < 3) { }
        }
    }
}
