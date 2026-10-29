using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Turso;
using TerraBase.Features.Memory;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

// Only revision indexes outlive requests after observations and caches have been drained.
// Subtract their actual admitted bytes, preserving exact assertions for every operation lease.
internal static class MemoryAccounting
{
    internal static long Retained(TerraBaseTestServer server, TenantContext? tenant = null) =>
        server.Services.GetRequiredService<TursoStorageEngine>().RetainedRevisionBytes(tenant);

    internal static long Available(TerraBaseTestServer server, long limit, TenantContext? tenant = null) =>
        limit - Retained(server, tenant);

    internal static IMemoryReservation? ReserveAvailable(TerraBaseTestServer server, MemoryLane lane, long limit, TenantContext? tenant = null) =>
        server.Services.GetRequiredService<IMemoryBudget>().TryReserve(lane, Available(server, limit, tenant), tenant);
}
