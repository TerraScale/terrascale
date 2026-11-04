using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    private const int RetentionRows = 16_384;
    private const long RetentionBytes = 64L * 1024 * 1024;
    private const string RetentionCut = "SELECT coalesce(max(change_id), 0) FROM turso_cdc WHERE change_id <= $below AND change_type = 2;";

    internal static bool Trim(SqliteConnection connection, SqliteTransaction? transaction, long below)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH handles AS (
                SELECT handle FROM _tb_shape_chunk WHERE handle >= 'h1-' AND handle < 'h2'
                UNION SELECT handle FROM _tb_shape_snapshot WHERE handle >= 'h1-' AND handle < 'h2'
                UNION SELECT substr(handle, 8) FROM _tb_shape_chunk WHERE handle >= '~floor:' AND handle < '~floor;' AND offset = 0
            )
            SELECT min(max(
                coalesce((SELECT end_offset FROM _tb_shape_chunk WHERE handle = '~floor:' || h.handle AND offset = 0), 0),
                coalesce((SELECT min(offset) FROM (
                    SELECT offset FROM _tb_shape_snapshot WHERE handle = h.handle
                    UNION ALL SELECT offset FROM _tb_shape_chunk WHERE handle = h.handle
                )), 0))) FROM handles h;
            """;
        if (command.ExecuteScalar() is long shapeFloor)
        {
            below = Math.Min(below, shapeFloor);
        }

        command.CommandText = "SELECT min(end_offset) FROM _tb_shape_chunk WHERE handle >= '~cdc:' AND handle < '~cdc;' AND offset = 0;";
        if (command.ExecuteScalar() is long consumerFloor)
        {
            below = Math.Min(below, consumerFloor);
        }

        command.CommandText = RetentionCut;
        command.Parameters.AddWithValue("$below", below);
        if (command.ExecuteScalar() is not long cut || cut == 0)
        {
            return false;
        }

        command.Parameters.Clear();
        command.CommandText = """
            SELECT change_id, coalesce(octet_length(before), 0) + coalesce(octet_length(after), 0) FROM turso_cdc
            WHERE change_id < $cut ORDER BY change_id LIMIT $rows;
            """;
        command.Parameters.AddWithValue("$cut", cut);
        command.Parameters.AddWithValue("$rows", RetentionRows + 1);
        var bound = cut;
        long bytes = 0;
        var rows = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                bytes += reader.GetInt64(1);
                if (rows == RetentionRows || (rows > 0 && bytes > RetentionBytes))
                {
                    bound = reader.GetInt64(0);
                    break;
                }

                rows++;
            }
        }

        command.Parameters.Clear();
        command.CommandText = "DELETE FROM turso_cdc WHERE change_id < $bound;";
        command.Parameters.AddWithValue("$bound", bound);
        _ = command.ExecuteNonQuery();
        return bound < cut;
    }

    public ValueTask<Ulid> GetIncarnationAsync(CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var text = Command(TursoStatements.Incarnation).ExecuteScalar() as string;
            if (text is not { Length: 26 } || text[0] is < '0' or > '7' || !Ulid.TryParse(text, out var incarnation)
                || incarnation == Ulid.Empty || incarnation.ToString() != text)
            {
                throw StoredDataCorruption.Create("the stored database incarnation is missing or invalid");
            }

            return incarnation;
        }, cancellationToken);

}
