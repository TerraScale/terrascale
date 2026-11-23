namespace TerraScale.Dashboard.Common.Accounts
{
    public static class AccountScope
    {
        public static async Task<AccountContext> RefreshAsync(AccountService accounts, AccountContext context,
            CancellationToken cancellationToken, bool requireProject = false, bool reload = false)
        {
            if (context.UserId != context.Session.UserId)
            {
                throw new AccountAccessException("Account identity does not match.", 401);
            }
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

            if (organization.RoleFor(fresh.UserId).Length == 0 && !(requireProject && ProjectCollaborator.Allows(organization, project)))
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
