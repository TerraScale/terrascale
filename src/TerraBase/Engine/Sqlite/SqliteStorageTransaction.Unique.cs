using System.Text;
using System.Text.Json;
using TerraBase.Engine.Lattices;
using TerraBase.Hosting;

namespace TerraBase.Engine.Sqlite;

internal sealed partial class SqliteStorageTransaction
{
    private const string UniqueVersion = """
        SELECT d.version, d.body IS NULL AND NOT EXISTS (
            SELECT 1 FROM unique_conflicts c WHERE c.collection = d.collection AND c.id = d.id)
        FROM documents d WHERE d.collection = $collection AND d.id = $id;
        """;
    private const string UniqueDefinitions = "SELECT body FROM documents WHERE collection = '_unique' AND id = $collection;";
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

    // A client drop tombstones the retained uniqueness losers too: their registers are live.
    private const string UniqueHiddenIds = "SELECT id FROM unique_conflicts WHERE collection = $collection ORDER BY id LIMIT $limit;";

    // Visibility only: the register and its winner's version stay as they are.
    private const string UniqueVisibleBody = "UPDATE documents SET body = $body WHERE collection = $collection AND id = $id;";

    public ValueTask<IReadOnlyList<UniqueConstraint>> GetUniqueConstraintsAsync(string collection, CancellationToken cancellationToken) =>
        RunAsync(() => ReadUniqueConstraints(collection), cancellationToken);

    private IReadOnlyList<UniqueConstraint> ReadUniqueConstraints(string collection)
    {
        if (Command(UniqueDefinitions, collection).ExecuteScalar() is not string body)
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
            DocumentMutation(replicated: false);
            if (!DatabaseName.TryParse(collection, out _) || !UniqueKeys.Valid(constraints))
            {
                throw new InvalidDataException("invalid unique schema");
            }

            var body = JsonSerializer.Serialize(new UniqueSchema(constraints), TerraBaseJsonContext.Default.UniqueSchema);
            if (Command(UniqueDefinitions, collection).ExecuteScalar() is string previous && previous == body)
            {
                return true;
            }

            // Declarations belong to a collection without live documents, a retained uniqueness loser included; consumers
            // must drop and recreate it for a changed schema. A drop's tombstones stay as registers and do not count.
            if (Command(UniqueLiveDocument, collection).ExecuteScalar() is not null)
            {
                throw new UniqueConstraintException();
            }

            Reserve(DocumentDelta.Unique, collection, Encoding.UTF8.GetByteCount(body));
            WriteMetadata(DocumentDelta.Unique, collection, body);
            return true;
        }, cancellationToken, mutation: true).AsTask());

    private StoredDocument? ReadUniqueDocument(string collection, string id, bool reserveResult = false)
    {
        var command = Command(SqliteStatements.UniqueRawDocument, collection);
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(1))
        {
            return null;
        }

        if (reserveResult)
        {
            ReserveJson(reader.GetInt64(3));
        }

        return new StoredDocument(id, reader.GetInt64(0), reader.GetFieldValue<byte[]>(1), Scope, UniqueConflict: reader.GetBoolean(2));
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
        return UniqueProjectionIds(collection, affected: true, cancellationToken);
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
                var conflict = Command(hidden ? """
                    INSERT INTO unique_conflicts (collection, id, body) SELECT collection, id, body FROM documents
                    WHERE collection = $collection AND id = $id;
                    """ : UniqueDeleteConflict, collection);
                conflict.Parameters.AddWithValue("$id", id);
                if (hidden)
                {
                    conflict.ExecuteNonQuery();
                }

                var visible = BodyCommand(UniqueVisibleBody, collection, id);
                visible.Parameters.AddWithValue("$body", hidden ? DBNull.Value : (object)StrictUtf8.GetString(raw.Json.Span));
                visible.ExecuteNonQuery();
                if (!hidden)
                {
                    conflict.ExecuteNonQuery();
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
        if (_imported)
        {
            ImportHistory(change);
        }
        else
        {
            Reserve(change.Collection, change.Id, change.Json?.Length ?? 0);
            AppendChange(change.Collection, change.Id, Offset(), change.Version,
                change.Json is { } json ? StrictUtf8.GetString(json.Span) : null, cancellationToken);
        }
    }

    private void ClearUniqueConflict(string collection, string id)
    {
        var command = Command(UniqueDeleteConflict, collection);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Worker-only: stores a replicated register whose last-writer-wins write changed, and projects the uniqueness of its
    // new winner (body, null for a tombstone) and of every contender it shares or shared a key with.
    private void StoreWinner(string collection, string id, MvRegister<DocumentWrite> state, byte[] encoded, string? body, CancellationToken cancellationToken)
    {
        var constraints = ReadUniqueConstraints(collection);
        if (constraints.Count == 0)
        {
            Store(collection, id, state, encoded);
            return;
        }

        QueueUniqueContenders(collection, id);
        IndexUniqueKeys(collection, id, body, constraints, local: false, cancellationToken);
        QueueUniqueContenders(collection, id);
        ClearUniqueConflict(collection, id);
        Store(collection, id, state, encoded);
        ReconcileUnique(collection, UniqueProjectionIds(collection, affected: true, cancellationToken), id, cancellationToken);
        Command("DELETE FROM temp.unique_affected;").ExecuteNonQuery();
    }

    private DocumentChange UniqueVisibleChange(DocumentChange change) =>
        ReadUniqueDocument(change.Collection, change.Id) is { UniqueConflict: true } ? change with { Json = null } : change;

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

    // The constraints of a replicated declaration; anything else fails the replicated group.
    private static IReadOnlyList<UniqueConstraint> ReadUniqueSchema(string body)
    {
        try
        {
            var schema = JsonSerializer.Deserialize(body, TerraBaseJsonContext.Default.UniqueSchema);
            return schema is not null && UniqueKeys.Valid(schema.Constraints)
                ? schema.Constraints
                : throw new InvalidDataException("invalid replicated unique schema");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("invalid replicated unique schema", error);
        }
    }

    // A declaration's last-writer-wins write changed (body, null for a tombstone): every state of the collection is indexed
    // again under it.
    private void ApplyUniqueMetadata(string collection, string? body, CancellationToken cancellationToken)
    {
        var constraints = body is null ? [] : ReadUniqueSchema(body);

        // Build all keys before projecting, so receiver size and arrival order cannot change the result.
        Command("DELETE FROM unique_keys WHERE collection = $collection;", collection).ExecuteNonQuery();
        Command("DELETE FROM unique_invalid WHERE collection = $collection;", collection).ExecuteNonQuery();
        foreach (var id in UniqueProjectionIds(collection, affected: false, cancellationToken))
        {
            var raw = ReadUniqueDocument(collection, id);
            IndexUniqueKeys(collection, id, raw is null ? null : StrictUtf8.GetString(raw.Json.Span), constraints, local: false, cancellationToken);
        }

        ReconcileUnique(collection, UniqueProjectionIds(collection, affected: false, cancellationToken), writtenId: null, cancellationToken);
        _metadataChanged = true;
    }
}
