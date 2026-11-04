using TerraBase.Errors;

namespace TerraBase.Features.Shapes;

internal sealed record ShapeGrant(string Table, string? Where, IReadOnlyList<string>? Columns, DateTimeOffset UpdatedAt);

internal readonly union ShapeGrantRead(ShapeGrant, NotFound, Unavailable);

internal readonly union ShapeGrantList(IReadOnlyList<ShapeGrant>, Unavailable);

internal readonly union ShapeGrantDelete(bool, Unavailable);

internal readonly union ShapeGrantPut(ShapeGrant, Unavailable);

internal interface IShapeGrants
{
    ValueTask<ShapeGrantRead> GetGrantAsync(string databaseId, string table, CancellationToken cancellationToken);

    ValueTask<ShapeGrantList> ListGrantsAsync(string databaseId, CancellationToken cancellationToken);

    ValueTask<ShapeGrantPut> PutGrantAsync(string databaseId, ShapeGrant grant, CancellationToken cancellationToken);

    ValueTask<ShapeGrantDelete> DeleteGrantAsync(string databaseId, string table, CancellationToken cancellationToken);
}
