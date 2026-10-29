using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal static class SystemDatabase
{
    internal static TenantContext Identity { get; } = new("_system", PlanTier.Hobby, "_system", "_system");
}
