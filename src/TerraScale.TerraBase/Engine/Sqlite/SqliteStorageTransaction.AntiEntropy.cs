using System.Text;

namespace TerraBase.Engine.Sqlite;

internal sealed partial class SqliteStorageTransaction
{
    private const int MaxReplicationStateKeys = 128;

    public ValueTask<ReplicationStatePage> ReadReplicationStateAsync(
        string? afterCollection, string? afterId, CancellationToken cancellationToken)
    {
        if ((afterCollection is null) != (afterId is null))
        {
            throw new ArgumentException("a replication state cursor needs both its collection and id", nameof(afterId));
        }

        return RunAsync(() =>
        {
            if (store.IsSystem)
            {
                throw new NotSupportedException("the system database replicates from its catalog primary");
            }

            // Registers in key order past keys no peer admits; sizes come from the record header.
            var command = Command(afterCollection is null ? SqliteStatements.ReplicationStateFirst : SqliteStatements.ReplicationStateAfter, afterCollection);
            if (afterId is not null)
            {
                command.Parameters.AddWithValue("$id", afterId);
            }

            command.Parameters.AddWithValue("$limit", MaxReplicationStateKeys + 1);
            command.Parameters.AddWithValue("$maxBytes", MaxSingleChangeBytes);
            var states = new List<DocumentDelta>();
            var unmaterialized = new List<DocumentDelta>();
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
                    return new ReplicationStatePage(states, HasMore: true, lastCollection, lastId, unmaterialized);
                }

                var collection = reader.GetString(0);
                var id = reader.GetString(1);
                var size = (long)Encoding.UTF8.GetByteCount(collection) + Encoding.UTF8.GetByteCount(id) + reader.GetInt64(3);
                if (reader.IsDBNull(2) || size > MaxSingleChangeBytes)
                {
                    throw new InvalidDataException("a stored document register is too large for a repair page");
                }

                // A page is applied in one transaction, under the limits of a replicated group.
                if (states.Count > 0 && size > IStorageTransaction.MaxChangeGroupBytes - bytes)
                {
                    return new ReplicationStatePage(states, HasMore: true, lastCollection, lastId, unmaterialized);
                }

                examined++;
                (lastCollection, lastId) = (collection, id);
                // A key no peer admits is examined and skipped: the cursor still moves past it.
                if (DocumentDelta.HasValidKey(collection, id))
                {
                    var state = new DocumentDelta(collection, id, Decode(reader.GetFieldValue<byte[]>(2)));
                    states.Add(state);
                    bytes += size;
                    if (!Materialized(collection, id, state.State))
                    {
                        unmaterialized.Add(state);
                    }
                }
            }

            return new ReplicationStatePage(states, HasMore: false, lastCollection, lastId, unmaterialized);
        }, cancellationToken, retry: true);
    }
}
