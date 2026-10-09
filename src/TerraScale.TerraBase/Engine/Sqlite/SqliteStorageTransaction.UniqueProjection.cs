using System.Text.Json;
using TerraBase.Primitives;

namespace TerraBase.Engine.Sqlite;

internal sealed partial class SqliteStorageTransaction
{
    private bool _uniqueAffected;
    private const string UniqueLiveDocument = """
        SELECT 1 FROM documents d WHERE d.collection = $collection
            AND (d.body IS NOT NULL OR EXISTS (
                SELECT 1 FROM unique_conflicts c WHERE c.collection = d.collection AND c.id = d.id)) LIMIT 1;
        """;

    private void IndexUniqueKeys(string collection, string id, string? body, IReadOnlyList<UniqueConstraint> constraints, bool local, CancellationToken cancellationToken)
    {
        var delete = Command(UniqueDeleteKeys, collection);
        delete.Parameters.AddWithValue("$id", id);
        delete.ExecuteNonQuery();
        delete = Command("DELETE FROM unique_invalid WHERE collection = $collection AND id = $id;", collection);
        delete.Parameters.AddWithValue("$id", id);
        delete.ExecuteNonQuery();
        if (body is null || constraints.Count == 0)
        {
            return;
        }

        // Admit every key before inserting any: an incompatible imported state claims no partial keys.
        var keys = new List<(string Name, byte[] Key)>();
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = DocumentJson.MaxStoredDepth });
            var bytes = 0;
            foreach (var constraint in constraints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (UniqueKeys.Key(document.RootElement, constraint) is not { } key)
                {
                    continue;
                }

                bytes += key.Length;
                if (bytes > UniqueKeys.MaxDocumentKeyBytes)
                {
                    throw new DocumentValidationException("unique keys of a document fit at most 64 KiB", tooLarge: true);
                }

                keys.Add((constraint.Name, key));
            }
        }
        catch (DocumentValidationException) when (!local)
        {
            var invalid = Command("INSERT INTO unique_invalid (collection, id) VALUES ($collection, $id);", collection);
            invalid.Parameters.AddWithValue("$id", id);
            invalid.ExecuteNonQuery();
            return;
        }

        foreach (var (name, key) in keys)
        {
            var insert = Command("INSERT INTO unique_keys (collection, constraint_name, value, id) VALUES ($collection, $name, $value, $id);", collection);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$value", key);
            insert.Parameters.AddWithValue("$id", id);
            insert.ExecuteNonQuery();
        }
    }

    private void BeginUniqueAffected()
    {
        // A group-commit savepoint can roll back table creation; do not cache its existence in managed state.
        Command("CREATE TEMP TABLE IF NOT EXISTS unique_affected (id TEXT PRIMARY KEY) WITHOUT ROWID;").ExecuteNonQuery();
        _uniqueAffected = true;
    }

    private void QueueUniqueContenders(string collection, string id)
    {
        BeginUniqueAffected();
        var command = Command("""
            INSERT OR IGNORE INTO temp.unique_affected (id)
            SELECT other.id FROM unique_keys mine JOIN unique_keys other
            ON other.collection = mine.collection AND other.constraint_name = mine.constraint_name AND other.value = mine.value
            WHERE mine.collection = $collection AND mine.id = $id;
            INSERT OR IGNORE INTO temp.unique_affected (id) VALUES ($id);
            """, collection);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Only ids are paged (128 KiB maximum); each source body is read and released individually.
    private IEnumerable<string> UniqueProjectionIds(string collection, bool affected, CancellationToken cancellationToken, long? beforeVersion = null)
    {
        var after = string.Empty;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = Command(affected ? """
                SELECT id FROM temp.unique_affected WHERE id > $after ORDER BY id LIMIT 128;
                """ : """
                SELECT d.id FROM documents d WHERE d.collection = $collection AND d.id > $after
                    AND ($before IS NULL OR (d.version < $before AND (d.body IS NOT NULL OR EXISTS (
                        SELECT 1 FROM unique_conflicts c WHERE c.collection = d.collection AND c.id = d.id))))
                ORDER BY d.id LIMIT 128;
                """, affected ? null : collection);
            command.Parameters.AddWithValue("$after", after);
            if (!affected)
            {
                command.Parameters.AddWithValue("$before", (object?)beforeVersion ?? DBNull.Value);
            }

            var ids = new List<string>(128);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ids.Add(reader.GetString(0));
                }
            }

            if (ids.Count == 0)
            {
                yield break;
            }

            foreach (var id in ids)
            {
                yield return id;
            }

            after = ids[^1];
        }
    }

    private void DropUniqueAffected()
    {
        if (_uniqueAffected)
        {
            Command("DROP TABLE IF EXISTS temp.unique_affected;").ExecuteNonQuery();
        }
    }
}
