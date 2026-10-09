using System.Diagnostics.Metrics;
using Microsoft.Data.Sqlite;
using TerraBase.Engine.Sqlite;
using TerraBase.Primitives;

namespace TerraBase.Testing;

/// <summary>Seeds and reads current-format database fixtures after real-server bootstrap.</summary>
public static class DocumentFiles
{
    /// <summary>Seeds a current-format database file at <paramref name="path"/> holding exactly these documents.</summary>
    public static async Task WriteAsync(string path, params (string Collection, string Id, string Body)[] documents)
    {
        using var meter = new Meter("TerraBase.Testing.DocumentFiles");
        await using var io = new SqliteIoPool(1, 1, meter);
        await SqliteDatabaseStore.InitializeAsync("fixture", path, io, string.Empty, afterBootstrap: null, CancellationToken.None);
        // Built, never interpolated: a path may contain ';' or quotes.
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        var system = Path.GetFileName(path) == "_system.db";
        await WriteDocumentsAsync(connection, documents, history: !system);
        if (system)
        {
            return;
        }

        await using var snapshot = connection.CreateCommand();
        snapshot.CommandText = """
            UPDATE commit_offset SET value = max(value, 1);
            UPDATE shape_history SET floor = 1;
            UPDATE replication_state SET complete = 0;
            """;
        await snapshot.ExecuteNonQueryAsync();
    }

    /// <summary>Replaces existing live document bodies in a closed database file without changing its schema.</summary>
    public static async Task UpdateAsync(string path, params (string Collection, string Id, string Body)[] documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync();
        // All or nothing: a missing document leaves the closed fixture file untouched.
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        foreach (var (collection, id, body) in documents)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE documents SET body = $body WHERE collection = $collection AND id = $id AND body IS NOT NULL;";
            update.Parameters.AddWithValue("$collection", collection);
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$body", body);
            if (await update.ExecuteNonQueryAsync() != 1)
            {
                throw new InvalidDataException("the fixture document does not exist");
            }
        }

        await transaction.CommitAsync();
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

    private static async Task WriteDocumentsAsync(SqliteConnection connection, (string Collection, string Id, string Body)[] documents, bool history)
    {
        ArgumentNullException.ThrowIfNull(documents);
        foreach (var (collection, id, body) in documents)
        {
            await using var metadata = connection.CreateCommand();
            metadata.CommandText = "INSERT INTO collections VALUES ($collection, 0, random(), $entityId) ON CONFLICT (name) DO NOTHING;";
            metadata.Parameters.AddWithValue("$collection", collection);
            metadata.Parameters.AddWithValue("$entityId", EntityIds.New().ToString());
            await metadata.ExecuteNonQueryAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO documents VALUES ($collection, $id, 1, $body) ON CONFLICT (collection, id) DO UPDATE SET body = excluded.body;";
            insert.Parameters.AddWithValue("$collection", collection);
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$body", body);
            await insert.ExecuteNonQueryAsync();
            if (!history)
            {
                continue;
            }

            await using var change = connection.CreateCommand();
            change.CommandText = """
                INSERT INTO change_log (collection, generation, id, version, body, document_version)
                SELECT $collection, generation, $id, 1, $body, 1 FROM collections WHERE name = $collection;
                """;
            change.Parameters.AddWithValue("$collection", collection);
            change.Parameters.AddWithValue("$id", id);
            change.Parameters.AddWithValue("$body", body);
            await change.ExecuteNonQueryAsync();
        }
    }
}
