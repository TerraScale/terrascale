using System.Text;
using System.Text.Json;
using TerraBase.Hosting;

namespace TerraBase.Engine.Sqlite;

internal sealed partial class SqliteStorageTransaction
{
    // A client document's stored version (NULL until its first request ends, see Version) and whether it is deleted:
    // neither in its collection's table ({0}) nor kept aside.
    private const string UniqueVersion = """
        SELECT (SELECT s.stamp FROM _tb_state s WHERE s.collection = $collection AND s.id = $id),
            NOT EXISTS (SELECT 1 FROM {0} WHERE id = $id) AND NOT EXISTS (SELECT 1 FROM unique_conflicts WHERE collection = $collection AND id = $id);
        """;

    // A client document as stored: its version (as UniqueVersion), its JSON (a hidden uniqueness loser's is kept aside),
    // whether it is hidden, and the JSON's size.
    private const string UniqueRawDocument = """
        SELECT (SELECT s.stamp FROM _tb_state s WHERE s.collection = $collection AND s.id = $id),
            coalesce(c.body, d.doc), c.body IS NOT NULL, coalesce(octet_length(c.body), octet_length(d.doc), 0)
        FROM (SELECT $id AS id) k LEFT JOIN {0} d ON d.id = k.id LEFT JOIN unique_conflicts c ON c.collection = $collection AND c.id = k.id;
        """;

    private const string UniqueDefinitions = "SELECT doc FROM \"_unique\" WHERE id = $collection;";
    private const string UniqueOtherContender = """
        SELECT 1 FROM unique_keys mine JOIN unique_keys other
        ON other.collection = mine.collection AND other.constraint_name = mine.constraint_name AND other.value = mine.value
        WHERE mine.collection = $collection AND mine.id = $id AND other.id != mine.id LIMIT 1;
        """;
    private const string UniqueLoses = """
        SELECT 1 FROM unique_keys mine JOIN unique_keys other
        ON other.collection = mine.collection AND other.constraint_name = mine.constraint_name AND other.value = mine.value
        WHERE mine.collection = $collection AND mine.id = $id AND other.id < mine.id
        UNION ALL SELECT 1 FROM unique_invalid WHERE collection = $collection AND id = $id LIMIT 1;
        """;
    private const string UniqueDeleteKeys = "DELETE FROM unique_keys WHERE collection = $collection AND id = $id;";
    private const string UniqueDeleteConflict = "DELETE FROM unique_conflicts WHERE collection = $collection AND id = $id;";

    public ValueTask<IReadOnlyList<UniqueConstraint>> GetUniqueConstraintsAsync(string collection, CancellationToken cancellationToken) =>
        RunAsync(() => ReadUniqueConstraints(collection), cancellationToken);

    private IReadOnlyList<UniqueConstraint> ReadUniqueConstraints(string collection)
    {
        if (store.IsSystem || Command(UniqueDefinitions, collection).ExecuteScalar() is not string body)
        {
            return [];
        }

        var schema = JsonSerializer.Deserialize(body, TerraBaseJsonContext.Default.UniqueSchema);
        return schema is not null && UniqueKeys.Valid(schema.Constraints)
            ? schema.Constraints
            : throw new InvalidDataException("invalid unique schema");
    }

    public ValueTask SetUniqueConstraintsAsync(string collection, IReadOnlyList<UniqueConstraint> constraints, CancellationToken cancellationToken) =>
        new(RunAsync(() =>
        {
            ThrowIfReadOnly();
            if (!DatabaseName.TryParse(collection, out _) || !UniqueKeys.Valid(constraints))
            {
                throw new InvalidDataException("invalid unique schema");
            }

            var body = JsonSerializer.Serialize(new UniqueSchema(constraints), TerraBaseJsonContext.Default.UniqueSchema);
            if (Command(UniqueDefinitions, collection).ExecuteScalar() is string previous && previous == Canonical(body))
            {
                return true;
            }

            // Declarations belong to a collection without live documents, a retained uniqueness loser included; consumers
            // must drop and recreate it for a changed schema.
            if (HasTable(collection) && TableCommand(UniqueLiveDocument, collection).ExecuteScalar() is not null)
            {
                throw new UniqueConstraintException();
            }

            Reserve(collection, Encoding.UTF8.GetByteCount(body));
            WriteMetadata(DocumentDelta.Unique, collection, body);
            return true;
        }, cancellationToken, mutation: true).AsTask());

    private StoredDocument? ReadUniqueDocument(string collection, string id, bool reserveResult = false)
    {
        if (!HasTable(collection))
        {
            return null;
        }

        using var reader = TableCommand(UniqueRawDocument, collection, id).ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(1))
        {
            return null;
        }

        if (reserveResult)
        {
            ReserveJson(reader.GetInt64(3));
        }

        return new StoredDocument(id, Version(collection, id, reader, 0), reader.GetFieldValue<byte[]>(1), Scope, UniqueConflict: reader.GetBoolean(2));
    }

    // All contenders retain their merged state. Visibility is a deterministic projection of the current keys.
    private IEnumerable<string> ReplaceUniqueKeys(string collection, string id, string? body, CancellationToken cancellationToken)
    {
        if (!Shaped)
        {
            return [];
        }

        var constraints = ReadUniqueConstraints(collection);
        if (constraints.Count == 0)
        {
            return [];
        }

        BeginUniqueAffected();
        Command("DELETE FROM temp.unique_affected;").ExecuteNonQuery();
        QueueUniqueContenders(collection, id);
        IndexUniqueKeys(collection, id, body, constraints, local: true, cancellationToken);
        var contenders = Command(UniqueOtherContender, collection);
        contenders.Parameters.AddWithValue("$id", id);
        if (contenders.ExecuteScalar() is not null)
        {
            throw new UniqueConstraintException();
        }

        QueueUniqueContenders(collection, id);
        return UniqueProjectionIds(cancellationToken);
    }

    private void ReconcileUnique(string collection, IEnumerable<string> ids, string? writtenId, CancellationToken cancellationToken)
    {
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = ReadUniqueDocument(collection, id);
            if (raw is null)
            {
                continue;
            }

            var loses = Command(UniqueLoses, collection);
            loses.Parameters.AddWithValue("$id", id);
            var hidden = loses.ExecuteScalar() is not null;
            if (hidden != raw.UniqueConflict)
            {
                // A hidden loser's JSON moves aside, out of its collection's table; a winner's moves back.
                if (hidden)
                {
                    TableCommand("""
                        INSERT INTO unique_conflicts (collection, id, body) SELECT $collection, id, doc FROM {0} WHERE id = $id;
                        """, collection, id).ExecuteNonQuery();
                }

                PutRow(collection, id, hidden ? null : StrictUtf8.GetString(raw.Json.Span));
                if (!hidden)
                {
                    ClearUniqueConflict(collection, id);
                }
            }

            if (hidden != raw.UniqueConflict && id != writtenId)
            {
                UniqueHistory(new DocumentChange(collection, id, raw.Version, hidden ? (ReadOnlyMemory<byte>?)null : raw.Json), cancellationToken);
            }
        }
    }

    private void UniqueHistory(DocumentChange change, CancellationToken cancellationToken)
    {
        Reserve(change.Id, change.Json?.Length ?? 0);
        AppendChange(change.Collection, change.Id, Offset(), change.Version,
            change.Json is { } json ? StrictUtf8.GetString(json.Span) : null, cancellationToken);
    }

    private void ClearUniqueConflict(string collection, string id)
    {
        var command = Command(UniqueDeleteConflict, collection);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private void DropUniqueCollection(string collection)
    {
        Command("DELETE FROM unique_keys WHERE collection = $collection;", collection).ExecuteNonQuery();
        Command("DELETE FROM unique_conflicts WHERE collection = $collection;", collection).ExecuteNonQuery();
        Command("DELETE FROM unique_invalid WHERE collection = $collection;", collection).ExecuteNonQuery();
        if (Command(UniqueDefinitions, collection).ExecuteScalar() is not null)
        {
            WriteMetadata(DocumentDelta.Unique, collection, null);
        }
    }
}
