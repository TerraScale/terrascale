using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    internal long RetainedRevisionBytes(TenantContext? tenant = null)
    {
        var retained = _stores.Values
            .Where(store => tenant is null || store.Tenant?.OrganizationId == tenant.OrganizationId)
            .Sum(store => store.Revisions.RetainedBytes);
        if (tenant is null)
        {
            retained += SystemStore?.Revisions.RetainedBytes ?? 0;
            retained += IntentStore?.Revisions.RetainedBytes ?? 0;
        }

        return retained;
    }
}
