using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replicas;

internal static class ReplicaRoutes
{
    public static TenantContext? Database(HttpContext context)
    {
        var (organization, project, id) = (Value(context, "organization"), Value(context, "project"), Value(context, "database"));
        return EntityIds.IsValid(organization) && EntityIds.IsValid(project) && EntityIds.IsValid(id)
            ? new TenantContext(organization, PlanTier.Hobby, project, id)
            : null;
    }

    public static string DatabasePath(TenantContext database, string? suffix = null) =>
        $"/v1/sync/organizations/{database.OrganizationId}/projects/{database.ProjectId}/databases/{database.DatabaseId}"
        + (suffix is null ? "" : "/" + suffix);

    private static string Value(HttpContext context, string name) => context.Request.RouteValues[name] as string ?? string.Empty;
}
