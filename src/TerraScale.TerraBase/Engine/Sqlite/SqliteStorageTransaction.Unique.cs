using System.Text;
using System.Text.Json;
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
        var command = Command(SqliteStatements.UniqueRawDocument, collection);
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(1)
            ? new StoredDocument(id, reader.GetInt64(0), reader.GetFieldValue<byte[]>(1), Scope, UniqueConflict: reader.GetBoolean(2))
            : null;
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
        if (_importedFromOffset is not null)
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

    private bool ApplyUniqueState(DocumentChange change, string? body, CancellationToken cancellationToken)
    {
        var constraints = ReadUniqueConstraints(change.Collection);
        if (constraints.Count == 0)
        {
            return ApplyState(change, body);
        }

        var command = Command(SqliteStatements.UniqueRawDocument, change.Collection);
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

        QueueUniqueContenders(change.Collection, change.Id);
        IndexUniqueKeys(change.Collection, change.Id, body, constraints, local: false, cancellationToken);
        QueueUniqueContenders(change.Collection, change.Id);
        ClearUniqueConflict(change.Collection, change.Id);
        var put = BodyCommand(SqliteStatements.Put, change.Collection, change.Id);
        put.Parameters.AddWithValue("$version", change.Version);
        put.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
        put.ExecuteNonQuery();
        ReconcileUnique(change.Collection, UniqueProjectionIds(change.Collection, affected: true, cancellationToken), change.Id, cancellationToken);
        Command("DELETE FROM temp.unique_affected;").ExecuteNonQuery();
        return true;
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
            WriteMetadata(DocumentChange.Unique, collection, null);
        }
    }

    private void DropUniqueStatesBefore(string collection, long version, CancellationToken cancellationToken)
    {
        var constraints = ReadUniqueConstraints(collection);
        if (constraints.Count == 0)
        {
            return;
        }

        BeginUniqueAffected();
        var command = Command("""
            INSERT OR IGNORE INTO temp.unique_affected (id)
            SELECT other.id FROM unique_keys mine JOIN unique_keys other
                ON other.collection = mine.collection AND other.constraint_name = mine.constraint_name AND other.value = mine.value
            JOIN documents d ON d.collection = mine.collection AND d.id = mine.id
            WHERE d.collection = $collection AND d.version < $version;
            """, collection);
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
        foreach (var id in UniqueProjectionIds(collection, affected: false, cancellationToken, beforeVersion: version))
        {
            IndexUniqueKeys(collection, id, body: null, constraints, local: false, cancellationToken);
            ClearUniqueConflict(collection, id);
            var deletion = new DocumentChange(collection, id, version, null);
            if (ApplyState(deletion, admittedBody: null))
            {
                UniqueHistory(deletion, cancellationToken);
            }
        }

        ReconcileUnique(collection, UniqueProjectionIds(collection, affected: true, cancellationToken), writtenId: null, cancellationToken);
        Command("DELETE FROM temp.unique_affected;").ExecuteNonQuery();
    }

    private bool ApplyUniqueMetadata(DocumentChange change, string? body, CancellationToken cancellationToken)
    {
        if (!DatabaseName.TryParse(change.Id, out _))
        {
            throw new InvalidDataException("a unique schema names an invalid collection");
        }

        IReadOnlyList<UniqueConstraint> constraints = [];
        if (body is not null)
        {
            try
            {
                var schema = JsonSerializer.Deserialize(body, TerraBaseJsonContext.Default.UniqueSchema);
                if (schema is null || !UniqueKeys.Valid(schema.Constraints))
                {
                    throw new InvalidDataException("invalid replicated unique schema");
                }

                constraints = schema.Constraints;
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

        // Build all keys before projecting, so receiver size and arrival order cannot change the result.
        Command("DELETE FROM unique_keys WHERE collection = $collection;", change.Id).ExecuteNonQuery();
        Command("DELETE FROM unique_invalid WHERE collection = $collection;", change.Id).ExecuteNonQuery();
        foreach (var id in UniqueProjectionIds(change.Id, affected: false, cancellationToken))
        {
            var raw = ReadUniqueDocument(change.Id, id);
            IndexUniqueKeys(change.Id, id, raw is null ? null : StrictUtf8.GetString(raw.Json.Span), constraints, local: false, cancellationToken);
        }

        ReconcileUnique(change.Id, UniqueProjectionIds(change.Id, affected: false, cancellationToken), writtenId: null, cancellationToken);
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
