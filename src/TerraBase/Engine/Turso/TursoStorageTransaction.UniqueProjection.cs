using System.Text.Json;
using TerraBase.Primitives;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    private bool _uniqueAffected;
    // A live document of a client collection ({0}), a retained uniqueness loser included.
    private const string UniqueLiveDocument = """
        SELECT 1 FROM {0} UNION ALL SELECT 1 FROM _tb_unique_conflicts WHERE collection = $collection LIMIT 1;
        """;

    private void IndexUniqueKeys(string collection, string id, string? body, IReadOnlyList<UniqueConstraint> constraints, bool local, CancellationToken cancellationToken)
    {
        var delete = Command(UniqueDeleteKeys, collection);
        delete.Parameters.AddWithValue("$id", id);
        delete.ExecuteNonQuery();
        delete = Command("DELETE FROM _tb_unique_invalid WHERE collection = $collection AND id = $id;", collection);
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
            var invalid = Command("INSERT INTO _tb_unique_invalid (collection, id) VALUES ($collection, $id);", collection);
            invalid.Parameters.AddWithValue("$id", id);
            invalid.ExecuteNonQuery();
            return;
        }

        foreach (var (name, key) in keys)
        {
            var insert = Command("INSERT INTO _tb_unique_keys (collection, constraint_name, value, id) VALUES ($collection, $name, $value, $id);", collection);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$value", key);
            insert.Parameters.AddWithValue("$id", id);
            insert.ExecuteNonQuery();
        }
    }

    // _tb_unique_affected is an ordinary table emptied before commit, not a TEMP one: change capture records a TEMP
    // table of the writer connection as if it were in the file, and a restore would replay it there.
    private void BeginUniqueAffected() => _uniqueAffected = true;

    private void QueueUniqueContenders(string collection, string id)
    {
        BeginUniqueAffected();
        var command = Command("""
            INSERT OR IGNORE INTO _tb_unique_affected (id)
            SELECT other.id FROM _tb_unique_keys mine JOIN _tb_unique_keys other
            ON other.collection = mine.collection AND other.constraint_name = mine.constraint_name AND other.value = mine.value
            WHERE mine.collection = $collection AND mine.id = $id;
            INSERT OR IGNORE INTO _tb_unique_affected (id) VALUES ($id);
            """, collection);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Only ids are paged (128 KiB maximum); each source body is read and released individually.
    private IEnumerable<string> UniqueProjectionIds(CancellationToken cancellationToken)
    {
        var after = string.Empty;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = Command("SELECT id FROM _tb_unique_affected WHERE id > $after ORDER BY id LIMIT 128;");
            command.Parameters.AddWithValue("$after", after);

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
            Command("DELETE FROM _tb_unique_affected;").ExecuteNonQuery();
        }
    }
}
