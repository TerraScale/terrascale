using System.Diagnostics.Metrics;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using Turso.Data.Sqlite;

namespace TerraBase.Testing;

/// <summary>Seeds and reads current-format database fixtures after real-server bootstrap.</summary>
public static class DocumentFiles
{
    /// <summary>
    /// Seeds a current-format database file at <paramref name="path"/> holding exactly these documents, as one PUT each
    /// (<see cref="StoredStates"/>); a client database in one commit at offset 1.
    /// </summary>
    public static async Task WriteAsync(string path, params (string Collection, string Id, string Body)[] documents)
    {
        using var meter = new Meter("TerraBase.Testing.DocumentFiles");
        await using var io = new TursoIoPool(1, 1, meter);
        var keys = new DatabaseKeys(new TestMasterKey());
        var system = Path.GetFileName(path) == "_system.db";
        if (system)
        {
            // A current-format catalog has its node-local operation journal published first.
            var journal = Path.Combine(Path.GetDirectoryName(path)!, TursoStorageEngine.IntentName + ".db");
            if (!File.Exists(journal))
            {
                await TursoDatabaseStore.InitializeAsync("_system", journal, io, string.Empty, keys, CancellationToken.None);
            }
        }
        // The name chooses the schema the file bootstraps: the system database keeps its documents in one table.
        await TursoDatabaseStore.InitializeAsync(system ? "_system" : "fixture", path, io, string.Empty, keys, CancellationToken.None);
        // Built, never interpolated: a path may contain ';' or quotes.
        await using var connection = new SqliteConnection(TestMasterKey.ConnectionString(path));
        await connection.OpenAsync();
        await WriteDocumentsAsync(connection, documents, system);
        if (system)
        {
            return;
        }

        await using var offset = connection.CreateCommand();
        offset.CommandText = "UPDATE commit_offset SET value = max(value, 1);";
        await offset.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Replaces existing live document bodies of a closed system database file without changing its schema: each becomes one
    /// PUT of its new body at the stamp its state row holds (<see cref="StoredStates.ReplaceAsync"/>), a healthy keyed row.
    /// </summary>
    public static async Task UpdateAsync(string path, params (string Collection, string Id, string Body)[] documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        await using var connection = new SqliteConnection(
            TestMasterKey.ConnectionString(path));
        await connection.OpenAsync();
        // All or nothing: a missing document leaves the closed fixture file untouched.
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        foreach (var (collection, id, body) in documents)
        {
            if (await StoredStates.ReplaceAsync(connection, collection, id, body, transaction, system: true) != 1)
            {
                throw new InvalidDataException("the fixture document does not exist");
            }
        }

        await transaction.CommitAsync();
    }

    /// <summary>
    /// Every live document of the system database file at <paramref name="path"/>, as "collection|id|body" lines in key order,
    /// bodies as the engine reads them; tombstones are left out.
    /// </summary>
    public static async Task<List<string>> ReadAsync(string path)
    {
        await using var connection = new SqliteConnection(
            TestMasterKey.ConnectionString(path, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT collection, id, body FROM documents ORDER BY collection, id;";
        await using var reader = await command.ExecuteReaderAsync();
        var documents = new List<string>();
        while (await reader.ReadAsync())
        {
            documents.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{StoredStates.SystemValue(reader.GetString(2))}");
        }

        return documents;
    }

    private static async Task WriteDocumentsAsync(SqliteConnection connection, (string Collection, string Id, string Body)[] documents, bool system)
    {
        ArgumentNullException.ThrowIfNull(documents);
        foreach (var (collection, id, body) in documents)
        {
            await using var metadata = connection.CreateCommand();
            metadata.CommandText = "INSERT INTO collections VALUES ($collection, 0, $entityId) ON CONFLICT (name) DO NOTHING;";
            metadata.Parameters.AddWithValue("$collection", collection);
            metadata.Parameters.AddWithValue("$entityId", EntityIds.New().ToString());
            await metadata.ExecuteNonQueryAsync();
            if (system)
            {
                await StoredStates.WriteAsync(connection, collection, id, body, system: true);
                continue;
            }

            await using (var table = connection.CreateCommand())
            {
                table.CommandText = TursoStorageTransaction.CreateTable(collection).Replace("CREATE TABLE", "CREATE TABLE IF NOT EXISTS", StringComparison.Ordinal);
                await table.ExecuteNonQueryAsync();
            }

            await StoredStates.WriteAsync(connection, collection, id, body);
        }
    }
}
