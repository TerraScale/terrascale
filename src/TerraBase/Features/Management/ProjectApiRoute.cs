namespace TerraBase.Features.Management;

public sealed class ProjectManagementRoute;

public sealed record LegacyManagementRoute(string OrganizationId, string OriginalPath);

internal static class ProjectApiRoute
{
    public const string Project = "/v1/projects/{project}";
    public const string Databases = Project + "/databases";
    public const string Database = Databases + "/{database}";
    public const string IdentityDescription =
        "Authorization: Bearer <ts-auth project access token>. project_id must equal {project}; scope must grant the operation. "
        + "{database} is the immutable database ULID or its name. A token narrowed to one database cannot reach another. "
        + "Tokens for another project or database answer 404.";
}
