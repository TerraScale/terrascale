using System.Security.Cryptography;
using System.Text;
using TerraBase.Engine;

namespace TerraBase.Features.Shapes;

internal readonly record struct ShapeHandle(string Handle, string ShapeId)
{
    public static ShapeHandle Compute(IDatabaseStore store, string table, string generation)
    {
        ArgumentNullException.ThrowIfNull(store);
        ObjectDisposedException.ThrowIf(store.IsClosed, store);
        var tenant = store.Tenant ?? throw new InvalidOperationException("a shape requires a tenant database");
        ArgumentException.ThrowIfNullOrEmpty(tenant.OrganizationId);
        ArgumentException.ThrowIfNullOrEmpty(tenant.ProjectId);
        ArgumentException.ThrowIfNullOrEmpty(tenant.DatabaseId);
        var fields = string.Join('\0', tenant.OrganizationId, tenant.ProjectId, tenant.DatabaseId, store.Incarnation.ToString(), table, generation, "2");
        return new ShapeHandle("h1-" + Digest("TB-SHAPE-HANDLE-V2", fields, 8), Digest("TB-SHAPE-V2", fields, 16));
    }

    public static ShapeSealDefinition Definition(IDatabaseStore store, ShapeScope scope)
    {
        var handle = Compute(store, scope.Table, scope.Generation);
        return new ShapeSealDefinition(handle.ShapeId, handle.Handle, scope.Table, scope.Generation, store.Incarnation);
    }

    private static string Digest(string domain, string fields, int bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(domain + '\0' + fields)).AsSpan(0, bytes));
}
