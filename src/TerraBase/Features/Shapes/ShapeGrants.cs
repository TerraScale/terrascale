using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Shapes;

internal sealed record ShapeGrant(string Table, string? Where, IReadOnlyList<string>? Columns, DateTimeOffset UpdatedAt);

internal readonly union ShapeGrantRead(ShapeGrant, NotFound, Unavailable);

internal readonly union ShapeGrantList(IReadOnlyList<ShapeGrant>, NotFound, Unavailable);

internal readonly union ShapeGrantDelete(bool, NotFound, Unavailable);

internal readonly union ShapeGrantPut(ShapeGrant, NotFound, Unavailable);

internal interface IShapeGrants
{
    ValueTask<ShapeGrantRead> GetGrantAsync(TenantContext database, string table, CancellationToken cancellationToken);

    ValueTask<ShapeGrantList> ListGrantsAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<ShapeGrantPut> PutGrantAsync(TenantContext database, ShapeGrant grant, CancellationToken cancellationToken);

    ValueTask<ShapeGrantDelete> DeleteGrantAsync(TenantContext database, string table, CancellationToken cancellationToken);
}
