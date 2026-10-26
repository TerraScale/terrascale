using System.Text;
using System.Text.Json;
using TerraBase.Hosting;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    // A client document's stored version (NULL until its first request ends, see Version) and whether it is deleted:
    // neither in its collection's table ({0}) nor kept aside.
    private const string UniqueVersion = """
        SELECT (SELECT s.stamp FROM _tb_state s WHERE s.collection = $collection AND s.id = $id),
            NOT EXISTS (SELECT 1 FROM {0} WHERE id = $id) AND NOT EXISTS (SELECT 1 FROM _tb_unique_conflicts WHERE collection = $collection AND id = $id);
        """;

    // A client document as stored: its version (as UniqueVersion), its JSON (a hidden uniqueness loser's is kept aside),
    // whether it is hidden, and the JSON's size.
    private const string UniqueRawDocument = """
        SELECT (SELECT s.stamp FROM _tb_state s WHERE s.collection = $collection AND s.id = $id),
            coalesce(c.body, d.doc), c.body IS NOT NULL, coalesce(octet_length(c.body), octet_length(d.doc), 0)
        FROM (SELECT $id AS id) k LEFT JOIN {0} d ON d.id = k.id LEFT JOIN _tb_unique_conflicts c ON c.collection = $collection AND c.id = k.id;
        """;

    private const string UniqueDefinitions = "SELECT doc FROM \"_unique\" WHERE id = $collection;";
    private const string UniqueOtherContender = """
        SELECT 1 FROM _tb_unique_keys mine JOIN _tb_unique_keys other
        ON other.collection = mine.collection AND other.constraint_name = mine.constraint_name AND other.value = mine.value
        WHERE mine.collection = $collection AND mine.id = $id AND other.id != mine.id LIMIT 1;
        """;
    private const string UniqueLoses = """
        SELECT 1 FROM _tb_unique_keys mine JOIN _tb_unique_keys other
        ON other.collection = mine.collection AND other.constraint_name = mine.constraint_name AND other.value = mine.value
        WHERE mine.collection = $collection AND mine.id = $id AND other.id < mine.id
        UNION ALL SELECT 1 FROM _tb_unique_invalid WHERE collection = $collection AND id = $id LIMIT 1;
        """;
    // A duplicate key, a document whose keys do not fit, or a hidden loser: a reindexed declaration does not hold.
    private const string UniqueReindexRefused = """
        SELECT 1 FROM _tb_unique_keys WHERE collection = $collection GROUP BY constraint_name, value HAVING count(*) > 1
        UNION ALL SELECT 1 FROM _tb_unique_invalid WHERE collection = $collection
        UNION ALL SELECT 1 FROM _tb_unique_conflicts WHERE collection = $collection LIMIT 1;
        """;
    private const string UniqueDeleteKeys = "DELETE FROM _tb_unique_keys WHERE collection = $collection AND id = $id;";
    private const string UniqueDeleteConflict = "DELETE FROM _tb_unique_conflicts WHERE collection = $collection AND id = $id;";

    public ValueTask<IReadOnlyList<UniqueConstraint>> GetUniqueConstraintsAsync(string collection, CancellationToken cancellationToken) =>
        RunAsync(() => ReadUniqueConstraints(collection), cancellationToken);

    private bool UniqueDefinitionActive(string collection) => CollectionDrop(collection) is not { } drop
        || (_pending.TryGetValue((DocumentDelta.Unique, collection), out var pending) ? pending.State : ReadState(DocumentDelta.Unique, collection, localWrite: true))
            .Clear?.Version > drop.Version;

    private IReadOnlyList<UniqueConstraint> ReadUniqueConstraints(string collection)
    {
        if (store.IsSystem || Command(UniqueDefinitions, collection).ExecuteScalar() is not string body
            || !UniqueDefinitionActive(collection))
        {
            return [];
        }

        var schema = JsonSerializer.Deserialize(body, TerraBaseJsonContext.Default.UniqueSchema);
        return schema is not null && UniqueKeys.Valid(schema.Constraints)
            ? schema.Constraints
            : throw new InvalidDataException("invalid unique schema");
    }

    public ValueTask SetUniqueConstraintsAsync(string collection, IReadOnlyList<UniqueConstraint> constraints, bool reindex, CancellationToken cancellationToken) =>
        new(RunAsync(() =>
        {
            ThrowIfReadOnly();
            if (!DatabaseName.TryParse(collection, out _) || !UniqueKeys.Valid(constraints))
            {
                throw new InvalidDataException("invalid unique schema");
            }

            var body = JsonSerializer.Serialize(new UniqueSchema(constraints), TerraBaseJsonContext.Default.UniqueSchema);
            if (Command(UniqueDefinitions, collection).ExecuteScalar() is string previous && previous == Canonical(body)
                && UniqueDefinitionActive(collection))
            {
                return true;
            }

            // Declarations belong to a collection without live documents, a retained uniqueness loser included; consumers
            // must drop and recreate it for a changed schema, unless they ask for a reindex of the documents.
            var live = HasTable(collection) && TableCommand(UniqueLiveDocument, collection).ExecuteScalar() is not null;
            if (live && !reindex)
            {
                throw new UniqueConstraintException();
            }

            Reserve(collection, Encoding.UTF8.GetByteCount(body));
            WriteMetadata(DocumentDelta.Unique, collection, body);
            if (live)
            {
                // The projection a replica runs for this declaration (ReprojectUnique); the caller rolls back on a throw.
                ReprojectUnique(collection, cancellationToken);
                if (Command(UniqueReindexRefused, collection).ExecuteScalar() is not null)
                {
                    throw new UniqueConstraintException();
                }
            }

            return true;
        }, cancellationToken, mutation: true).AsTask());

    private StoredDocument? ReadUniqueDocument(string collection, string id, bool reserveResult = false, string path = "point")
    {
        if (!HasTable(collection))
        {
            return null;
        }

        CountRead(path);
        using var reader = TableCommand(UniqueRawDocument, collection, id).ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(1))
        {
            return null;
        }

        if (reserveResult)
        {
            ReserveJson(reader.GetInt64(3));
        }

        return new StoredDocument(id, Version(collection, id, reader, 0), TursoSession.Bytes(reader, 1), Scope, UniqueConflict: reader.GetBoolean(2));
    }

    // All contenders retain their merged state. Visibility is a deterministic projection of the current keys.
    // A local write that collides throws; a replicated join never fails a constraint, its loser is hidden instead.
    // `constraints`, when given, are the collection's as ReadUniqueConstraints returns them in this transaction.
    private IEnumerable<string> ReplaceUniqueKeys(string collection, string id, string? body, CancellationToken cancellationToken,
        bool local = true, IReadOnlyList<UniqueConstraint>? constraints = null)
    {
        if (!IsClient)
        {
            return [];
        }

        constraints ??= ReadUniqueConstraints(collection);
        if (constraints.Count == 0)
        {
            return [];
        }

        BeginUniqueAffected();
        Command("DELETE FROM _tb_unique_affected;").ExecuteNonQuery();
        QueueUniqueContenders(collection, id);
        IndexUniqueKeys(collection, id, body, constraints, local, cancellationToken);
        var contenders = Command(UniqueOtherContender, collection);
        contenders.Parameters.AddWithValue("$id", id);
        if (local && contenders.ExecuteScalar() is not null)
        {
            throw new UniqueConstraintException();
        }

        QueueUniqueContenders(collection, id);
        return UniqueProjectionIds(cancellationToken);
    }

    private void ReconcileUnique(string collection, IEnumerable<string> ids, CancellationToken cancellationToken)
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
                        INSERT INTO _tb_unique_conflicts (collection, id, body) SELECT $collection, id, doc FROM {0} WHERE id = $id;
                        """, collection, id).ExecuteNonQuery();
                }

                PutRow(collection, id, hidden ? null : StrictUtf8.GetString(raw.Json.Span));
                if (!hidden)
                {
                    ClearUniqueConflict(collection, id);
                }
            }
        }
    }

    private void ClearUniqueConflict(string collection, string id)
    {
        var command = Command(UniqueDeleteConflict, collection);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // A local drop also deletes the collection's constraints document; a replicated one leaves that to its own delta.
    private void DropUniqueCollection(string collection, bool local)
    {
        Command("DELETE FROM _tb_unique_keys WHERE collection = $collection;", collection).ExecuteNonQuery();
        Command("DELETE FROM _tb_unique_conflicts WHERE collection = $collection;", collection).ExecuteNonQuery();
        Command("DELETE FROM _tb_unique_invalid WHERE collection = $collection;", collection).ExecuteNonQuery();
        // The system catalog has no client _unique collection or declared client constraints.
        if (local && !store.IsSystem && Command(UniqueDefinitions, collection).ExecuteScalar() is not null)
        {
            WriteMetadata(DocumentDelta.Unique, collection, null);
        }
    }
}
