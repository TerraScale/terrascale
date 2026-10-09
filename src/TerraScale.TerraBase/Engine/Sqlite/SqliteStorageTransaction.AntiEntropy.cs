using System.Text;

namespace TerraBase.Engine.Sqlite;

internal sealed partial class SqliteStorageTransaction
{
    private const int MaxReplicationStateKeys = 128;
    private long _restoredBodyBytes;

    public ValueTask<ReplicationStatePage> ReadReplicationStateAsync(
        string? afterCollection, string? afterId, CancellationToken cancellationToken)
    {
        if ((afterCollection is null) != (afterId is null))
        {
            throw new ArgumentException("a replication state cursor needs both its collection and id", nameof(afterId));
        }

        return RunAsync(() =>
        {
            var command = Command(afterCollection is null ? SqliteStatements.ReplicationStateFirst : SqliteStatements.ReplicationStateAfter, afterCollection);
            if (afterId is not null)
            {
                command.Parameters.AddWithValue("$id", afterId);
            }

            command.Parameters.AddWithValue("$limit", MaxReplicationStateKeys + 1);
            var changes = new List<DocumentChange>();
            var repairs = new List<DocumentChange>();
            var examined = 0;
            var lastCollection = afterCollection;
            var lastId = afterId;
            long bytes = 0;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (examined == MaxReplicationStateKeys)
                {
                    return new ReplicationStatePage(changes, HasMore: true, repairs, lastCollection, lastId);
                }

                var collection = reader.GetString(0);
                var id = reader.GetString(1);
                if (!DocumentChange.HasValidKey(collection, id))
                {
                    examined++;
                    (lastCollection, lastId) = (collection, id);
                    continue;
                }

                var bodyBytes = reader.GetInt64(4);
                var keyBytes = reader.GetInt64(5);
                if (bodyBytes is < 0 or > MaxDocumentBytes || keyBytes < 0
                    || keyBytes > IStorageTransaction.MaxChangeGroupBytes - bodyBytes)
                {
                    throw new InvalidDataException("a stored replication state exceeds the document or page byte limits");
                }

                var retained = RetainedBody(collection, id);
                var rowBytes = keyBytes + (retained?.Bytes ?? bodyBytes);
                if (rowBytes > IStorageTransaction.MaxChangeGroupBytes - bytes)
                {
                    return new ReplicationStatePage(changes, HasMore: true, repairs, lastCollection, lastId);
                }

                // The ordered key query holds no bodies; fetch a live body only after byte admission.
                ReadOnlyMemory<byte>? json = reader.GetBoolean(3) ? (ReadOnlyMemory<byte>?)null
                    : ReadUniqueDocument(collection, id)?.Json ?? throw new InvalidDataException("a repair state lost its body");
                var restoreBody = false;
                if (retained is { } history)
                {
                    var canonical = RetainedBodyBytes(history.Sequence);
                    restoreBody = json is null || !canonical.Span.SequenceEqual(json.Value.Span);
                    json = canonical;
                }

                var change = new DocumentChange(collection, id, reader.GetInt64(2), json);
                changes.Add(change);
                if (reader.GetBoolean(6) || restoreBody)
                {
                    repairs.Add(change);
                }
                examined++;
                (lastCollection, lastId) = (collection, id);
                bytes += rowBytes;
            }

            return new ReplicationStatePage(changes, HasMore: false, repairs, lastCollection, lastId);
        }, cancellationToken, retry: true);
    }

    public ValueTask<bool> RepairReplicationStateAsync(DocumentChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return RunAsync(() =>
        {
            var restored = RestoreRetainedBody(change);
            // Restoration and merge share one gate slot; commit/disposal must wait for both.
            var merged = Apply(change, cancellationToken);
            return restored || merged;
        }, cancellationToken, mutation: true);
    }

    private bool RestoreRetainedBody(DocumentChange change)
    {
        ThrowIfReadOnly();
        DocumentMutation(replicated: true);
        if (!DocumentChange.HasValidKey(change.Collection, change.Id))
        {
            throw new InvalidDataException("a repair state has an invalid collection or id");
        }

        // Re-read inside the writer: an earlier scan must not roll back a concurrent winner.
        if (RetainedBody(change.Collection, change.Id) is not { } retained)
        {
            return false;
        }

        var body = Admit(RetainedBodyBytes(retained.Sequence));
        var restore = BodyCommand(SqliteStatements.RestoreReplicationBody, change.Collection, change.Id);
        restore.Parameters.AddWithValue("$version", retained.Version);
        restore.Parameters.AddWithValue("$body", body);
        if (restore.ExecuteNonQuery() == 0)
        {
            return false;
        }

        // A repair page also bounds local restoration work. Overflow faults the transaction,
        // rolling it back; a subsequent digest repairs these bodies in their own bounded pages.
        _restoredBodyBytes += retained.Bytes;
        if (_restoredBodyBytes > IStorageTransaction.MaxChangeGroupBytes)
        {
            throw new InvalidOperationException("a repair transaction restores at most 2 MiB of retained bodies");
        }

        return true;
    }

    private (long Sequence, long Version, long Bytes)? RetainedBody(string collection, string id)
    {
        if (!Shaped || collection is DocumentChange.Collections or DocumentChange.Indexes or DocumentChange.Unique)
        {
            return null;
        }

        // Accepted winners append their history at commit, so retained evidence is stale for this key.
        if (_importHistory)
        {
            var pending = Command("SELECT 1 FROM temp.imported_history WHERE collection = $collection AND id = $id LIMIT 1;", collection);
            pending.Parameters.AddWithValue("$id", id);
            if (pending.ExecuteScalar() is not null)
            {
                return null;
            }
        }

        var command = Command(SqliteStatements.ReplicationRetainedBody, collection);
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var bytes = reader.GetInt64(2);
        var currentBytes = reader.GetInt64(3);
        if (bytes is < 0 or > MaxDocumentBytes || currentBytes is < 0 or > MaxDocumentBytes)
        {
            throw new InvalidDataException("a retained repair body exceeds the document byte limit");
        }

        return (reader.GetInt64(0), reader.GetInt64(1), bytes);
    }

    private ReadOnlyMemory<byte> RetainedBodyBytes(long sequence)
    {
        var command = Command(SqliteStatements.ReplicationRetainedBodyBytes);
        command.Parameters.AddWithValue("$sequence", sequence);
        return Encoding.UTF8.GetBytes((string)command.ExecuteScalar()!);
    }
}
