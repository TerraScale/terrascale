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
            if (update.Run() == 0)
            {
                var insert = Command(SqliteStatements.InsertApplied);
                insert.Parameters.AddWithValue("$node", node);
                insert.Parameters.AddWithValue("$applied", Version(applied));
                insert.Parameters.AddWithValue("$seen", seen);
                insert.Run();
            }

            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Worker-only, inside a mutation.
    private bool Join(string collection, string id, DocumentState delta, EntryVersion at, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly();
        if (store.IsSystem)
        {
            throw new NotSupportedException("the system database does not join replicated deltas");
        }

        Member.Clock.Observe(at.Stamp);
        if (!HasTable(collection))
        {
            // A dropped collection's metadata document keeps its deletion: entries below it are dominated (section 5). A
            // newer write brings the collection back, as a write after a delete brings a document back.
            if (ReadState(DocumentDelta.Collections, collection).Clear is { Deleted: true } dropped
                && !new DocumentState(dropped, DocumentState.Bottom.Fields).Join(delta).Exists)
            {
                return false;
            }

            // The collection's own metadata document comes later (another origin made it); it then sets the identity.
            AddCollection(collection, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), EntityIds.New().ToString());
        }

        var state = ReadState(collection, id);
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
        var offset = Offset();
        PutRow(collection, id, materialized);
        WriteState(collection, id, next, at);
        if (metadata)
        {
            if (collection == DocumentDelta.Collections)
            {
                CollectionMetadata(id, materialized);
            }

            return true;
        }

        ReconcileUnique(collection, affected, id, cancellationToken);
        AppendChange(collection, id, offset, Version(at.Stamp), materialized, cancellationToken);
        return true;
    }

    // Worker-only: the local DDL of a collection's metadata document. A live document creates the collection, or gives the
    // existing one its identity; a deleted one drops it here, without writing metadata of its own.
    private void CollectionMetadata(string name, string? body)
    {
        if (CollectionName.IsReserved(name) || !DatabaseName.TryParse(name, out _))
        {
            return;
        }

        var exists = Command(SqliteStatements.GetCollection, name).ExecuteScalar() is not null;
        if (body is null)
        {
            if (exists)
            {
                DropTable(name);
                Command(SqliteStatements.DropCollection, name).Run();
                DropUniqueCollection(name, local: false);
                _collectionsChanged = true;
            }

            return;
        }

        using var parsed = JsonDocument.Parse(body, DocumentState.JsonOptions);
        var created = parsed.RootElement.TryGetProperty("created_at", out var at) && at.TryGetInt64(out var ms) ? ms : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entity = parsed.RootElement.TryGetProperty("entity_id", out var value) && value.GetString() is { } text && EntityIds.IsValid(text)
            ? text
            : EntityIds.New().ToString();
        if (!exists)
        {
            AddCollection(name, created, entity);
            return;
        }

        var update = Command(SqliteStatements.UpdateCollection, name);
        update.Parameters.AddWithValue("$created", created);
        update.Parameters.AddWithValue("$entityId", entity);
        update.Run();
        _collectionsChanged = true;
    }

    // Worker-only: a client collection's row and table, without a metadata document of this member's own.
    private void AddCollection(string name, long created, string entity)
    {
        var command = Command(SqliteStatements.CreateCollection, name);
        command.Parameters.AddWithValue("$created", created);
        command.Parameters.AddWithValue("$entityId", entity);
        command.Run();
        using (var table = PlainCommand(CreateTable(name)))
        {
            table.Run();
        }

        _collectionsChanged = true;
    }
}
