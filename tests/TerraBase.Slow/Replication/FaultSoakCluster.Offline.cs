using System.Text.Json.Nodes;
using TerraBase.Engine.Sqlite;
using Turso.Data.Sqlite;

namespace TerraBase.Slow.Replication;

internal sealed partial class FaultSoakCluster
{
    public async Task<JsonNode?> ReadStoppedDocumentAsync(string region, string id, CancellationToken cancellationToken)
    {
        var node = _nodes[region];
        if (node.Client is not null || node.Process is { HasExited: false })
        {
            throw new InvalidOperationException($"{region} must be stopped before inspecting its database");
        }

        var source = Path.Combine(node.Data, _organization, _project, _database + ".db");
        if (!File.Exists(source))
        {
            return null;
        }
        var snapshot = Directory.CreateTempSubdirectory("terrabase-soak-inspect-").FullName;
        try
        {
            var database = Path.Combine(snapshot, "database.db");
            // Recovery and connection disposal may touch sidecars: inspect a copy so the node's original bytes stay intact.
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                if (File.Exists(source + suffix))
                {
                    File.Copy(source + suffix, database + suffix);
                }
            }

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = $name;";
            command.Parameters.AddWithValue("$name", Collection);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 0)
            {
                return null;
            }

            command.Parameters.Clear();
            command.CommandText = $"SELECT doc FROM {SqliteStorageTransaction.Table(Collection)} WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? JsonNode.Parse(SqliteSession.Bytes(reader, 0).AsSpan()) : null;
        }
        finally
        {
            Directory.Delete(snapshot, recursive: true);
        }
    }
}
