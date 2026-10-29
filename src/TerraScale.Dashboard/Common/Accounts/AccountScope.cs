namespace TerraScale.Dashboard.Common.Accounts
{
    // Explicit tool scopes do not change the browser selection. Re-read provider
    // membership before resolving the requested organization and project.
    public static class AccountScope
    {
        public static async Task<AccountContext> RefreshAsync(AccountService accounts, AccountContext context,
            CancellationToken cancellationToken, bool requireProject = false, bool reload = false)
        {
            if (context.UserId != context.Session.UserId)
            {
                throw new AccountAccessException("Account identity does not match.", 401);
            }
            // A context this request already loaded from ts-auth, with no write sent since, is the answer a reload would give;
            // only a context whose organization and project are its own loaded entries qualifies.
            // reload re-reads after a change this request made outside the dashboard's own clients.
            var fresh = !reload && context.Loaded?.Current == true && (context.Organization is null || context.Organizations.Contains(context.Organization)) &&
                (context.Project is null || context.Organization?.Projects.GetValueOrDefault(context.Project.Id) == context.Project)
                ? context : await accounts.GetContextAsync(context.Session, cancellationToken, context.Organization?.Id, context.Project?.Id);
            if (fresh.UserId != context.UserId)
            {
                throw new AccountAccessException("Account identity does not match.", 401);
            }

            var organization = fresh.Organizations.SingleOrDefault(item => item.Id == context.Organization?.Id)
                ?? throw new AccountAccessException("Organization is unavailable.", 404);
            var project = context.Project is null ? null : organization.Projects.GetValueOrDefault(context.Project.Id);
            if (requireProject && project is null || context.Project is not null && project is null ||
                project is not null && (project.OrganizationId != organization.Id || project.Status != "active"))
            {
                throw new AccountAccessException("Project is unavailable.", 404);
            }

            if (organization.RoleFor(fresh.UserId).Length == 0 && !(requireProject && !organization.MfaRequired &&
                organization.Status == "active" && !organization.Archived && (project?.Role is "viewer" or "member" or "admin")))
            {
                throw new AccountAccessException("Organization membership is required.");
            }

            if (organization.ContainsMember(fresh.UserId) && organization.Id != fresh.Organization?.Id)
            {
                await accounts.HydrateAccessAsync(organization, fresh.Session, cancellationToken);
            }

            return fresh with { Organization = organization, Project = project };
        }
    }
}
