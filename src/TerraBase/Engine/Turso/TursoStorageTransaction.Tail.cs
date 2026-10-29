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
        command.CommandText = RetentionCut;
        command.Parameters.AddWithValue("$below", below);
        if (command.ExecuteScalar() is not long cut || cut == 0)
        {
            return false;
        }

        command.Parameters.Clear();
        command.CommandText = """
            SELECT change_id, coalesce(octet_length(after), 0) FROM turso_cdc
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
