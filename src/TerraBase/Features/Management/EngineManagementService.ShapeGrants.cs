using TerraBase.Features.Shapes;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService : IShapeGrants
{
    public ValueTask<ShapeGrantRead> GetGrantAsync(string databaseId, string table, CancellationToken cancellationToken) =>
        ReadAsync<ShapeGrantRead>(async transaction =>
            (await CatalogTables.ShapeGrantsAsync(transaction, databaseId, table, cancellationToken).ConfigureAwait(false)) is [var grant]
                ? grant
                : new Errors.NotFound("the table has no read grant", "no_read_grant"),
            unavailable => unavailable, cancellationToken);

    public ValueTask<ShapeGrantList> ListGrantsAsync(string databaseId, CancellationToken cancellationToken) =>
        ReadAsync<ShapeGrantList>(async transaction =>
            new ShapeGrantList(await CatalogTables.ShapeGrantsAsync(transaction, databaseId, null, cancellationToken).ConfigureAwait(false)),
            unavailable => unavailable, cancellationToken);

    public ValueTask<ShapeGrantPut> PutGrantAsync(string databaseId, ShapeGrant grant, CancellationToken cancellationToken) =>
        WriteAsync<ShapeGrantPut>(async transaction =>
        {
            _ = await CatalogTables.PutShapeGrantAsync(transaction, databaseId, grant, cancellationToken).ConfigureAwait(false);
            return grant;
        }, unavailable => unavailable, cancellationToken);

    public ValueTask<ShapeGrantDelete> DeleteGrantAsync(string databaseId, string table, CancellationToken cancellationToken) =>
        WriteAsync<ShapeGrantDelete>(async transaction =>
            await CatalogTables.DeleteShapeGrantAsync(transaction, databaseId, table, cancellationToken).ConfigureAwait(false),
            unavailable => unavailable, cancellationToken);
}
