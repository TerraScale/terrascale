using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replicas;

internal static class HomeMoveRoute
{
    public const string Database = "/v1/sync/organizations/{organization}/projects/{project}/databases/{database}";

    public static TenantContext? Of(HttpContext context)
    {
        var (organization, project, database) = (Value(context, "organization"), Value(context, "project"), Value(context, "database"));
        return EntityIds.IsValid(organization) && EntityIds.IsValid(project) && EntityIds.IsValid(database)
            ? new TenantContext(organization, PlanTier.Hobby, project, database)
            : null;
    }

    private static string Value(HttpContext context, string name) => context.Request.RouteValues[name] as string ?? string.Empty;
}
