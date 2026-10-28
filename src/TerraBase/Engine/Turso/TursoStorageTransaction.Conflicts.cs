using System.Text.Json;
using TerraBase.Primitives;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    public ValueTask<IReadOnlyList<(string Constraint, string Id)>> GetUniqueConflictsAsync(
        string collection, ReadOnlyMemory<byte> json, int limit, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<(string Constraint, string Id)>>(() =>
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IStorageTransaction.MaxChangeGroupChanges + 1);
            using var document = JsonDocument.Parse(json);
            var conflicts = new List<(string, string)>();
            var bytes = 0;
            foreach (var constraint in ReadUniqueConstraints(collection))
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

                var command = Command("""
                    SELECT id FROM _tb_unique_keys
                    WHERE collection = $collection AND constraint_name = $name AND value = $value
                    ORDER BY id LIMIT $limit;
                    """, collection);
                command.Parameters.AddWithValue("$name", constraint.Name);
                command.Parameters.AddWithValue("$value", key);
                command.Parameters.AddWithValue("$limit", limit - conflicts.Count);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ReserveJson(1024); // Bound each retained constraint name and document id before allocating it.
                    conflicts.Add((constraint.Name, reader.GetString(0)));
                }

                if (conflicts.Count == limit)
                {
                    break;
                }
            }

            return conflicts;
        }, cancellationToken);
}
