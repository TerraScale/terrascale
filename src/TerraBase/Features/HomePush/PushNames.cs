using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.HomePush;

internal static class PushNames
{
    public static string Stream(string origin) => "TERRABASE_PUSH_" + origin.ToUpperInvariant();

    public static string Subjects(string origin) => $"terrabase.push.{origin}.>";

    public static string Subject(string origin, string home, TenantContext database) =>
        $"terrabase.push.{origin}.{home}.{database.OrganizationId}.{database.ProjectId}.{database.DatabaseId}";

    public static string HomeFilter(string origin, string home) => $"terrabase.push.{origin}.{home}.>";

    public static string Consumer(string home) => "home-" + home;

    public static string Applied(string origin, TenantContext database) =>
        $"terrabase.applied.{origin}.{database.OrganizationId}.{database.ProjectId}.{database.DatabaseId}";

    public static string AppliedAll(string origin) => $"terrabase.applied.{origin}.>";

    public static TenantContext? Database(string subject)
    {
        var parts = subject.Split('.');
        return parts.Length >= 5 && EntityIds.IsValid(parts[^3]) && EntityIds.IsValid(parts[^2]) && EntityIds.IsValid(parts[^1])
            ? new TenantContext(parts[^3], PlanTier.Hobby, parts[^2], parts[^1])
            : null;
    }
}
