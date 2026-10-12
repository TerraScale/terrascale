namespace TerraScale.Dashboard.Features.Accounts;

// Explicit tool scopes do not change the browser selection. Re-read provider
// membership before resolving the requested organization and project.
public static class AccountScope
{
    public static async Task<AccountContext> RefreshAsync(AccountService accounts, AccountContext context,
        CancellationToken cancellationToken, bool requireProject = false)
    {
        if (context.UserId != context.Session.UserId) throw new AccountAccessException("Account identity does not match.", 401);
        var fresh = await accounts.GetContextAsync(context.Session, cancellationToken);
        if (fresh.UserId != context.UserId) throw new AccountAccessException("Account identity does not match.", 401);
        var organization = fresh.Organizations.SingleOrDefault(item => item.Id == context.Organization?.Id)
            ?? throw new AccountAccessException("Organization is unavailable.", 404);
        if (organization.RoleFor(fresh.UserId).Length == 0) throw new AccountAccessException("Organization membership is required.");
        var project = context.Project is null ? null : organization.Projects.GetValueOrDefault(context.Project.Id);
        if (requireProject && project is null || context.Project is not null && project is null ||
            project is not null && project.OrganizationId != organization.Id)
            throw new AccountAccessException("Project is unavailable.", 404);
        return fresh with { Organization = organization, Project = project };
    }
}
