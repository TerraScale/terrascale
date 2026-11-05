namespace TerraBase.Features.Management;

/// <summary>Marks project-token management operations, whose database parameter is an immutable ULID or current name.</summary>
public sealed class ProjectManagementRoute;

/// <summary>The identity and original path from a hidden organization-route alias.</summary>
/// <param name="OrganizationId">The organization named by the legacy route.</param>
/// <param name="OriginalPath">The original alias, retained when forwarding to the catalog primary.</param>
public sealed record LegacyManagementRoute(string OrganizationId, string OriginalPath);

/// <summary>The project hierarchy of the public API (docs/api.md).</summary>
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
