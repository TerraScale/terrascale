using System.Text.Json;
using TerraBase.Engine.Lattices;
using TerraBase.Primitives;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// The receive path of the peer streams (docs/terrabase/multi-region-replication.md section 9): a replicated delta joins
/// into the document's state, and the document is written only when the join changed it, under the delta's stamp and
/// origin, so the change feed and the shipper see whose operation it was and never ship it back.
/// </summary>
internal sealed partial class SqliteStorageTransaction
{
    public async ValueTask<bool> JoinAsync(string collection, string id, DocumentState delta, EntryVersion at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delta);
        return await RunAsync(() => Join(collection, id, delta, at, cancellationToken), cancellationToken, mutation: true).ConfigureAwait(false);
    }

    public async ValueTask RecordAppliedAsync(ReplicaId origin, Hlc applied, CancellationToken cancellationToken)
    {
        _ = await RunAsync(() =>
        {
            ThrowIfReadOnly();
            var node = unchecked((long)origin.Value);
            var seen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var update = Command(SqliteStatements.UpdateApplied);
            update.Parameters.AddWithValue("$node", node);
            update.Parameters.AddWithValue("$applied", Version(applied));
            update.Parameters.AddWithValue("$seen", seen);
            if (update.ExecuteNonQuery() == 0)
            {
                var insert = Command(SqliteStatements.InsertApplied);
                insert.Parameters.AddWithValue("$node", node);
                insert.Parameters.AddWithValue("$applied", Version(applied));
                insert.Parameters.AddWithValue("$seen", seen);
                insert.ExecuteNonQuery();
            }

            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Worker-only, inside a mutation.
    private bool Join(string collection, string id, DocumentState delta, EntryVersion at, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly();
        Member.Clock.Observe(at.Stamp);
        var state = ReadState(collection, id);
        if (state.MaxStamp.Value == 0 && CollectedBefore() is { Value: > 0 } floor)
        {
            var clear = new DocumentState(CollectedClear(floor), DocumentState.Bottom.Fields);
            delta = clear.Join(delta);
            if (delta.Equals(clear))
            {
                return false;
            }
        }

        ClearEntry? collectionDrop = null;
        if (IsClient && !IsMetadata(collection) && ReadState(DocumentDelta.Collections, collection).Clear is { Deleted: true } dropped)
        {
            collectionDrop = dropped;
            var clear = new DocumentState(dropped, DocumentState.Bottom.Fields);
            delta = clear.Join(delta);
            if (delta.Equals(clear))
            {
                return false;
            }
        }

        if (!HasTable(collection))
        {
            if (collectionDrop is { } drop)
            {
                AddDroppedCollection(collection, drop, cancellationToken);
            }
            else
            {
                // The collection's own metadata document comes later (another origin made it); it then sets the identity.
                AddCollection(collection, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), EntityIds.New().ToString());
            }
        }

        var next = state.Join(delta);
        if (next.Equals(state))
        {
            return false;
        }

        var materialized = Materialize(next);
        var metadata = IsMetadata(collection);
        var affected = metadata ? [] : ReplaceUniqueKeys(collection, id, materialized, cancellationToken, local: false);
        ClearUniqueConflict(collection, id);
        cancellationToken.ThrowIfCancellationRequested();
        Offset();
        PutRow(collection, id, materialized);
        WriteState(collection, id, next, at);
        if (metadata)
        {
            if (collection == DocumentDelta.Collections)
            {
                CollectionMetadata(id, materialized, next, state.Clear, cancellationToken);
            }
            else if (collection == DocumentDelta.Indexes)
            {
                IndexDefinitionMetadata(id, materialized, cancellationToken);
            }
            else if (collection == DocumentDelta.Unique)
            {
                ReprojectUnique(id, cancellationToken);
            }

            return true;
        }

        ReconcileUnique(collection, affected, cancellationToken);
        return true;
    }

    // Worker-only: project collection identity and clear covered entries without emitting new metadata operations.
    private void CollectionMetadata(string name, string? body, DocumentState state, ClearEntry? previousClear, CancellationToken cancellationToken)
    {
        if (CollectionName.IsReserved(name) || !DatabaseName.TryParse(name, out _))
        {
            return;
        }

        var exists = Command(SqliteStatements.GetCollection, name).ExecuteScalar() is not null;
        if (exists && state.Clear is { Deleted: true } drop && state.Clear != previousClear)
        {
            DropCoveredCollection(name, drop, cancellationToken);
            exists = HasTable(name);
        }

        if (body is null)
        {
            if (exists && state.Clear is { Deleted: true } bodyDrop)
            {
                SetDroppedCollectionIdentity(name, bodyDrop);
                ChangedReaderState = true;
            }

            return;
        }

        using var parsed = JsonDocument.Parse(body, DocumentState.JsonOptions);
        var created = parsed.RootElement.TryGetProperty("created_at", out var at) && at.TryGetInt64(out var ms) ? ms
            : state.Clear is { Deleted: true } deleted ? deleted.Version.Stamp.Milliseconds : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entity = parsed.RootElement.TryGetProperty("entity_id", out var value) && value.GetString() is { } text && EntityIds.IsValid(text)
            ? text
            : state.Clear is { Deleted: true } deletedClear ? DroppedCollectionId(deletedClear) : EntityIds.New().ToString();
        if (!exists)
        {
            AddCollection(name, created, entity);
            if (state.Clear is { Deleted: true } droppedClear)
            {
                RestoreDroppedCollectionIndexes(name, droppedClear, cancellationToken);
            }

            return;
        }

        var update = Command(SqliteStatements.UpdateCollection, name);
        update.Parameters.AddWithValue("$created", created);
        update.Parameters.AddWithValue("$entityId", entity);
        update.ExecuteNonQuery();
        ChangedReaderState = true;
    }

    // Worker-only: the local DDL of an index's metadata document. A live one builds the index where it is missing, on its
    // collection (made without a metadata document of this member's own when absent), and replaces an index of that name
    // with other fields or on another collection: a redefinition seen only as its final state, or a concurrent creation
    // elsewhere that won the join, so every replica holds the document's definition. A deleted one drops it. A definition
    // this build cannot read changes nothing here: the document still replicates, and refusing it would stall the stream.
    private void IndexDefinitionMetadata(string name, string? body, CancellationToken cancellationToken)
    {
        if (body is null)
        {
            _ = DropIndex(name, cancellationToken);
            return;
        }

        using var parsed = JsonDocument.Parse(body, DocumentState.JsonOptions);
        if (!parsed.RootElement.TryGetProperty("collection", out var named) || named.GetString() is not { } collection
            || !DatabaseName.TryParse(collection, out _) || CollectionName.IsReserved(collection)
            || !parsed.RootElement.TryGetProperty("fields", out var paths) || paths.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        List<DocumentField> fields = [];
        foreach (var path in paths.EnumerateArray())
        {
            if (path.ValueKind != JsonValueKind.String || !DocumentField.TryParse(path.GetString(), out var field))
            {
                return;
            }

            fields.Add(field);
        }

        if (ReadState(DocumentDelta.Collections, collection).Clear is { Deleted: true } drop
            && (!HasTable(collection) || !(ReadState(DocumentDelta.Indexes, name).Clear?.Version > drop.Version)))
        {
            _ = DropIndex(name, cancellationToken);
            return;
        }

        if (ReadIndexes(cancellationToken).Find(index => index.Name == name) is { } existing)
        {
            if (existing.Collection == collection && existing.Fields.SequenceEqual(fields))
            {
                return;
            }

            _ = DropIndex(name, cancellationToken);
        }

        if (!HasTable(collection))
        {
            AddCollection(collection, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), EntityIds.New().ToString());
        }

        try
        {
            _ = CreateIndex(new IndexDefinition(name, collection, fields), cancellationToken);
        }
        catch (ArgumentException)
        {
            // Not an index this build can make (its name or fields): see above. An older one of that name is gone, as the
            // document no longer defines it.
        }
    }

    // Worker-only: a replicated constraints document may arrive after documents it constrains, which this machine took in
    // without it. So its join re-projects the whole collection under the constraints it now holds: every live document and
    // retained loser gets its keys again, then visibility follows the deterministic rule (smallest id wins), as on every
    // replica. Without constraints every loser comes back.
    private void ReprojectUnique(string collection, CancellationToken cancellationToken)
    {
        if (!HasTable(collection))
        {
            return;
        }

        var constraints = ReadUniqueConstraints(collection);
        Command("DELETE FROM _tb_unique_keys WHERE collection = $collection;", collection).ExecuteNonQuery();
        Command("DELETE FROM _tb_unique_invalid WHERE collection = $collection;", collection).ExecuteNonQuery();
        BeginUniqueAffected();
        Command("DELETE FROM _tb_unique_affected;").ExecuteNonQuery();
        TableCommand("INSERT OR IGNORE INTO _tb_unique_affected (id) SELECT id FROM {0};", collection).ExecuteNonQuery();
        Command("INSERT OR IGNORE INTO _tb_unique_affected (id) SELECT id FROM _tb_unique_conflicts WHERE collection = $collection;", collection).ExecuteNonQuery();
        foreach (var id in UniqueProjectionIds(cancellationToken))
        {
            if (ReadUniqueDocument(collection, id) is { } document)
            {
                IndexUniqueKeys(collection, id, StrictUtf8.GetString(document.Json.Span), constraints, local: false, cancellationToken);
            }
        }

        ReconcileUnique(collection, UniqueProjectionIds(cancellationToken), cancellationToken);
    }

    // Worker-only: a client collection's row and table, without a metadata document of this member's own.
    private void AddCollection(string name, long created, string entity)
    {
        var command = Command(SqliteStatements.CreateCollection, name);
        command.Parameters.AddWithValue("$created", created);
        command.Parameters.AddWithValue("$entityId", entity);
        command.ExecuteNonQuery();
        using (var table = PlainCommand(CreateTable(name)))
        {
            table.ExecuteNonQuery();
        }

        ChangedReaderState = true;
    }

    private void RestoreDroppedCollectionIndexes(string collection, ClearEntry drop, CancellationToken cancellationToken)
    {
        var after = string.Empty;
        while (true)
        {
            var command = TableCommand("SELECT id FROM {0} WHERE id > $after ORDER BY id LIMIT 128;", DocumentDelta.Indexes);
            command.Parameters.AddWithValue("$after", after);
            List<string> ids = [];
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
                return;
            }

            foreach (var id in ids)
            {
                var state = ReadState(DocumentDelta.Indexes, id);
                if (state.Clear?.Version > drop.Version && Materialize(state) is { } body)
                {
                    using var parsed = JsonDocument.Parse(body, DocumentState.JsonOptions);
                    if (parsed.RootElement.TryGetProperty("collection", out var named) && named.GetString() == collection)
                    {
                        IndexDefinitionMetadata(id, body, cancellationToken);
                    }
                }
            }

            after = ids[^1];
        }
    }
}
