using System.Text;
using System.Text.Json;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Engine.Sqlite;

internal sealed partial class SqliteStorageTransaction
{
    private const string UniqueVersion = """
        SELECT d.version, d.body IS NULL AND NOT EXISTS (
            SELECT 1 FROM unique_conflicts c WHERE c.collection = d.collection AND c.id = d.id)
        FROM documents d WHERE d.collection = $collection AND d.id = $id;
        """;
    private const string UniqueDefinitions = "SELECT body FROM documents WHERE collection = '_unique' AND id = $collection;";
    private const string UniqueRawDocument = """
        SELECT d.version, coalesce(c.body, d.body), c.body IS NOT NULL
        FROM documents d LEFT JOIN unique_conflicts c ON c.collection = d.collection AND c.id = d.id
        WHERE d.collection = $collection AND d.id = $id;
        """;
    private const string UniqueContenders = """
        SELECT DISTINCT other.id FROM unique_keys mine JOIN unique_keys other
        ON other.collection = mine.collection AND other.constraint_name = mine.constraint_name AND other.value = mine.value
        WHERE mine.collection = $collection AND mine.id = $id LIMIT 1025;
        """;
    private const string UniqueLoses = """
        SELECT 1 FROM unique_keys mine JOIN unique_keys other
        ON other.collection = mine.collection AND other.constraint_name = mine.constraint_name AND other.value = mine.value
        WHERE mine.collection = $collection AND mine.id = $id AND other.id < mine.id LIMIT 1;
        """;
    private const string UniqueDeleteKeys = "DELETE FROM unique_keys WHERE collection = $collection AND id = $id;";
    private const string UniqueDeleteConflict = "DELETE FROM unique_conflicts WHERE collection = $collection AND id = $id;";

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

            // Declarations belong to an empty collection; consumers must recreate it for a changed schema.
            if (Command("SELECT 1 FROM documents WHERE collection = $collection LIMIT 1;", collection).ExecuteScalar() is not null)
            {
                throw new UniqueConstraintException();
            }

            Reserve(DocumentChange.Unique, collection, Encoding.UTF8.GetByteCount(body));
            WriteMetadata(DocumentChange.Unique, collection, body);
            return true;
        }, cancellationToken, mutation: true).AsTask());

    private StoredDocument? ReadUniqueDocument(string collection, string id)
    {
        var command = Command(UniqueRawDocument, collection);
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(1)
            ? new StoredDocument(id, reader.GetInt64(0), reader.GetFieldValue<byte[]>(1), Scope, UniqueConflict: reader.GetBoolean(2))
            : null;
    }

    // All contenders retain their merged state. Visibility is a deterministic projection of the current keys.
    private HashSet<string> ReplaceUniqueKeys(string collection, string id, string? body, bool local, CancellationToken cancellationToken)
    {
        if (!Shaped)
        {
            return [];
        }

        var affected = UniqueContenderIds(collection, id, cancellationToken);
        var constraints = ReadUniqueConstraints(collection);
        if (constraints.Count == 0 && affected.Count == 0)
        {
            return [];
        }

        var delete = Command(UniqueDeleteKeys, collection);
        delete.Parameters.AddWithValue("$id", id);
        delete.ExecuteNonQuery();
        if (body is not null)
        {
            if (constraints.Count > 0)
            {
                using var document = JsonDocument.Parse(body);
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

                    var insert = Command("INSERT INTO unique_keys (collection, constraint_name, value, id) VALUES ($collection, $name, $value, $id);", collection);
                    insert.Parameters.AddWithValue("$name", constraint.Name);
                    insert.Parameters.AddWithValue("$value", key);
                    insert.Parameters.AddWithValue("$id", id);
                    insert.ExecuteNonQuery();
                }
            }
        }

        var contenders = UniqueContenderIds(collection, id, cancellationToken);
        affected.UnionWith(contenders);
        if (affected.Count > MaxWrites)
        {
            throw new StorageWriteLimitException("a uniqueness conflict affects at most 1024 documents");
        }

        if (local && contenders.Any(other => other != id))
        {
            throw new UniqueConstraintException();
        }

        affected.Add(id);
        if (affected.Count > MaxWrites)
        {
            throw new StorageWriteLimitException("a uniqueness conflict affects at most 1024 documents");
        }
        return affected;
    }

    private HashSet<string> UniqueContenderIds(string collection, string id, CancellationToken cancellationToken)
    {
        var command = Command(UniqueContenders, collection);
        command.Parameters.AddWithValue("$id", id);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ids.Count == MaxWrites)
            {
                throw new StorageWriteLimitException("a uniqueness conflict affects at most 1024 documents");
            }

            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private void ReconcileUnique(string collection, IEnumerable<string> ids, string? writtenId, CancellationToken cancellationToken)
    {
        long bytes = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = ReadUniqueDocument(collection, id);
            if (raw is null)
            {
                continue;
            }

            bytes += raw.Json.Length;
            if (bytes > IStorageTransaction.MaxChangeGroupBytes)
            {
                throw new StorageWriteLimitException("uniqueness reconciliation fits at most 2 MiB");
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

                var visible = BodyCommand(SqliteStatements.Put, collection, id);
                visible.Parameters.AddWithValue("$version", raw.Version);
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
        Reserve(change.Collection, change.Id, change.Json?.Length ?? 0);
        if (_importedFromOffset is not null)
        {
            _appliedChanges.Add(change);
        }
        else
        {
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

    private bool ApplyUniqueState(DocumentChange change, string? body, CancellationToken cancellationToken)
    {
        if (ReadUniqueConstraints(change.Collection).Count == 0)
        {
            return ApplyState(change, body);
        }

        var command = Command(UniqueRawDocument, change.Collection);
        command.Parameters.AddWithValue("$id", change.Id);
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                var version = reader.GetInt64(0);
                if (version > change.Version || (version == change.Version
                    && (reader.IsDBNull(1) || (body is not null && Encoding.UTF8.GetBytes(body).AsSpan().SequenceCompareTo(reader.GetFieldValue<byte[]>(1)) <= 0))))
                {
                    return false;
                }
            }
        }

        var affected = ReplaceUniqueKeys(change.Collection, change.Id, body, local: false, cancellationToken);
        ClearUniqueConflict(change.Collection, change.Id);
        var put = BodyCommand(SqliteStatements.Put, change.Collection, change.Id);
        put.Parameters.AddWithValue("$version", change.Version);
        put.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
        put.ExecuteNonQuery();
        ReconcileUnique(change.Collection, affected, change.Id, cancellationToken);
        return true;
    }

    private DocumentChange UniqueVisibleChange(DocumentChange change) =>
        ReadUniqueDocument(change.Collection, change.Id) is { UniqueConflict: true } ? change with { Json = null } : change;

    private void DropUniqueCollection(string collection)
    {
        Command("DELETE FROM unique_keys WHERE collection = $collection;", collection).ExecuteNonQuery();
        Command("DELETE FROM unique_conflicts WHERE collection = $collection;", collection).ExecuteNonQuery();
        if (Command(UniqueDefinitions, collection).ExecuteScalar() is not null)
        {
            WriteMetadata(DocumentChange.Unique, collection, null);
        }
    }

    private void DropUniqueStatesBefore(string collection, long version, CancellationToken cancellationToken)
    {
        if (ReadUniqueConstraints(collection).Count == 0)
        {
            return;
        }

        var command = Command("""
            SELECT d.id FROM documents d
            WHERE d.collection = $collection AND d.version < $version
                AND (d.body IS NOT NULL OR EXISTS (
                    SELECT 1 FROM unique_conflicts c WHERE c.collection = d.collection AND c.id = d.id))
            LIMIT 1025;
            """, collection);
        command.Parameters.AddWithValue("$version", version);
        var ids = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ids.Count == MaxWrites)
                {
                    throw new StorageWriteLimitException("a replicated unique collection drop affects at most 1024 documents");
                }

                ids.Add(reader.GetString(0));
            }
        }

        foreach (var id in ids)
        {
            var deletion = new DocumentChange(collection, id, version, null);
            if (ApplyUniqueState(deletion, body: null, cancellationToken))
            {
                UniqueHistory(deletion, cancellationToken);
            }
        }
    }

    private bool ApplyUniqueMetadata(DocumentChange change, string? body, CancellationToken cancellationToken)
    {
        if (!DatabaseName.TryParse(change.Id, out _))
        {
            throw new InvalidDataException("a unique schema names an invalid collection");
        }

        if (body is not null)
        {
            try
            {
                var schema = JsonSerializer.Deserialize(body, TerraBaseJsonContext.Default.UniqueSchema);
                if (schema is null || !UniqueKeys.Valid(schema.Constraints))
                {
                    throw new InvalidDataException("invalid replicated unique schema");
                }
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("invalid replicated unique schema", error);
            }
        }

        if (!ApplyState(change, body))
        {
            return false;
        }

        // Replaying a declaration projects the receiver's current states, including writes from other origins.
        var ids = new List<string>();
        var command = Command("""
            SELECT d.id, coalesce(octet_length(c.body), octet_length(d.body), 0)
            FROM documents d LEFT JOIN unique_conflicts c ON c.collection = d.collection AND c.id = d.id
            WHERE d.collection = $collection LIMIT 1025;
            """, change.Id);
        long bytes = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytes += reader.GetInt64(1);
                if (ids.Count == MaxWrites || bytes > IStorageTransaction.MaxChangeGroupBytes)
                {
                    throw new StorageWriteLimitException("a changed unique schema affects at most 1024 documents and 2 MiB");
                }

                ids.Add(reader.GetString(0));
            }
        }

        foreach (var id in ids)
        {
            var raw = ReadUniqueDocument(change.Id, id);
            _ = ReplaceUniqueKeys(change.Id, id, raw is null ? null : StrictUtf8.GetString(raw.Json.Span), local: false, cancellationToken);
        }

        ReconcileUnique(change.Id, ids, writtenId: null, cancellationToken);
        _metadataChanged = true;
        return true;
    }

    // Baselines replicate source states, including conflict losers, rather than the query projection.
    private const string UniqueBaseline = """
        SELECT d.collection, d.id, d.version,
            CASE WHEN coalesce(octet_length(c.body), octet_length(d.body), 0) <= $maxBytes THEN coalesce(c.body, d.body) END,
            coalesce(octet_length(c.body), octet_length(d.body), 0)
        FROM documents d LEFT JOIN unique_conflicts c ON c.collection = d.collection AND c.id = d.id
        WHERE octet_length(d.collection) <= 63 AND octet_length(d.id) <= 1024
        """;
    private const string UniqueBaselineLiveFirst = UniqueBaseline + " AND coalesce(c.body, d.body) IS NOT NULL ORDER BY d.collection, d.id LIMIT $limit;";
    private const string UniqueBaselineLiveAfter = UniqueBaseline + " AND (d.collection, d.id) > ($collection, $id) AND coalesce(c.body, d.body) IS NOT NULL ORDER BY d.collection, d.id LIMIT $limit;";
    private const string UniqueBaselineDeletedFirst = UniqueBaseline + " AND coalesce(c.body, d.body) IS NULL ORDER BY d.collection, d.id LIMIT $limit;";
    private const string UniqueBaselineDeletedAfter = UniqueBaseline + " AND (d.collection, d.id) > ($collection, $id) AND coalesce(c.body, d.body) IS NULL ORDER BY d.collection, d.id LIMIT $limit;";
}
