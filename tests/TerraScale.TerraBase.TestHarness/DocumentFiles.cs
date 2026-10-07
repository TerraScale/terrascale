using Microsoft.Data.Sqlite;

namespace TerraBase.Testing;

/// <summary>Writes and reads database files directly, the way an older build left them on disk.</summary>
public static class DocumentFiles
{
    /// <summary>Writes a schema-version-1 database file at <paramref name="path"/> holding exactly these documents.</summary>
    public static async Task WriteAsync(string path, params (string Collection, string Id, string Body)[] documents)
    {
        // Built, never interpolated: a path may contain ';' or quotes.
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        await WriteDocumentsAsync(connection, documents);
    }

    /// <summary>Every live document of the file at <paramref name="path"/>, as "collection|id|body" lines in key order; tombstones are left out.</summary>
    public static async Task<List<string>> ReadAsync(string path)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT collection, id, body FROM documents WHERE body IS NOT NULL ORDER BY collection, id;";
        await using var reader = await command.ExecuteReaderAsync();
        var documents = new List<string>();
        while (await reader.ReadAsync())
        {
            documents.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}");
        }

        return documents;
    }

    private static async Task WriteDocumentsAsync(SqliteConnection connection, (string Collection, string Id, string Body)[] documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS documents (collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT NOT NULL, PRIMARY KEY (collection, id)) WITHOUT ROWID;
                PRAGMA user_version = 1;
                """;
            await schema.ExecuteNonQueryAsync();
        }

        foreach (var (collection, id, body) in documents)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO documents VALUES ($collection, $id, 1, $body);";
            insert.Parameters.AddWithValue("$collection", collection);
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$body", body);
            await insert.ExecuteNonQueryAsync();
        }
    }
}
