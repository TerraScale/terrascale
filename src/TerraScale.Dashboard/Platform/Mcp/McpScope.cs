using TerraScale.Dashboard.Features.Accounts;

namespace TerraScale.Dashboard.Platform.Mcp;

internal static class McpScope
{
    internal static AccountContext Account(HttpContext? http) => http?.Items[DashboardMcp.ContextKey] as AccountContext ?? throw new AccountAccessException("Authentication is required.", 401);

    internal static AccountContext Scope(HttpContext? http, string? organizationId, string? projectId)
    {
        var context = Account(http);
        var organization = string.IsNullOrEmpty(organizationId) ? context.Organization : context.Organizations.SingleOrDefault(item => item.Id == organizationId);
        if (organization is null || organization.RoleFor(context.UserId).Length == 0) throw new AccountAccessException("Organization is unavailable.", 404);
        var project = string.IsNullOrEmpty(projectId)
            ? organization.Id == context.Organization?.Id ? context.Project : organization.Projects.Values.FirstOrDefault(item => item.IsDefault)
            : organization.Projects.GetValueOrDefault(projectId);
        if (!string.IsNullOrEmpty(projectId) && project is null || project is not null && project.OrganizationId != organization.Id)
            throw new AccountAccessException("Project is unavailable.", 404);
        return new(context.Session, context.Account, context.Organizations, organization, project);
    }

    internal static string Alias(string primary, string? alias, string name, bool required = true)
    {
        if (primary.Length > 0 && !string.IsNullOrEmpty(alias) && primary != alias) throw new AccountAccessException("Conflicting " + name + " values were provided.", 422);
        var result = primary.Length > 0 ? primary : alias ?? "";
        if (required && result.Length == 0) throw new AccountAccessException(name + " is required.", 422);
        return result;
    }

    internal static void DemandManagement(AccountContext context)
    { if (!context.CanManage) throw new AccountAccessException("Organization management access is required."); }
}

public sealed record McpDeleted(bool Deleted);
