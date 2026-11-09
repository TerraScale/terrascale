using Microsoft.Extensions.DependencyInjection;
using TerraBase.Tenancy;

namespace TerraBase.Testing;

internal static class TenantDatabases
{
    public static async Task<TenantContext> ContextAsync(this Tenant tenant, TerraBaseTestServer server, string name, CancellationToken cancellationToken = default)
    {
        using var response = await tenant.Data.PutAsync(tenant.Database(name), null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await server.Services.GetRequiredService<ITenantDirectory>().FindDatabaseAsync(
            tenant.OrganizationId, tenant.ProjectId, name, cancellationToken) switch
        {
            TenantContext database => database,
            _ => throw new InvalidOperationException("The database created through the data listener was not found in the tenant directory."),
        };
    }
}
