using TerraBase.Features.Shapes;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    public ValueTask<ShapeGrant?> ReadShapeGrantAsync(string table, CancellationToken cancellationToken) =>
        RunAsync(() => ShapeReader.Grant(table), cancellationToken);

    public ValueTask<IReadOnlyList<ShapeGrant>> ListShapeGrantsAsync(CancellationToken cancellationToken) =>
        RunAsync(() => ShapeReader.Grants(), cancellationToken);

    public async ValueTask PutShapeGrantAsync(ShapeGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        await RunAsync(() =>
        {
            var upsert = Command("""
                INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ($key, 0, $updated, $body, 0)
                ON CONFLICT(handle, offset) DO UPDATE SET end_offset = excluded.end_offset, body = excluded.body;
                """);
            upsert.Parameters.AddWithValue("$key", TursoShapeReader.ShapeGrantPrefix + grant.Table);
            upsert.Parameters.AddWithValue("$updated", grant.UpdatedAt.UtcTicks);
            upsert.Parameters.AddWithValue("$body", TursoShapeReader.GrantBody(grant));
            upsert.ExecuteNonQuery();
            upsert.Parameters.Clear();
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    public async ValueTask<bool> DeleteShapeGrantAsync(string table, CancellationToken cancellationToken)
    {
        return await RunAsync(() =>
        {
            var remove = Command("DELETE FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
            remove.Parameters.AddWithValue("$key", TursoShapeReader.ShapeGrantPrefix + table);
            var removed = remove.ExecuteNonQuery() > 0;
            remove.Parameters.Clear();
            return removed;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }
}
