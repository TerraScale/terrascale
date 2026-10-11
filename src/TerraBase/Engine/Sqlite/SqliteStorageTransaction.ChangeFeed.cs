namespace TerraBase.Engine.Sqlite;

internal sealed partial class SqliteStorageTransaction
{
    public ValueTask<CollectionChangePage> ReadCollectionChangesAsync(
        string collection, long afterOffset, long afterSequence, int limit, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentOutOfRangeException.ThrowIfNegative(afterOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 2 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, 8 * 1024 * 1024);

        return RunAsync(() => WithQueryBudget(() =>
        {
            using var command = Uncached(collection);
            command.CommandText = """
                SELECT version, sequence, id, document_version, body,
                       octet_length(id), coalesce(octet_length(body), 0)
                FROM change_log INDEXED BY change_log_by_offset
                WHERE collection = $collection
                  AND generation = (SELECT generation FROM collections WHERE name = $collection)
                  AND (version, sequence) > ($offset, $sequence)
                ORDER BY version, sequence
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$offset", afterOffset);
            command.Parameters.AddWithValue("$sequence", afterSequence);
            command.Parameters.AddWithValue("$limit", limit + 1);
            var changes = new List<CollectionChange>();
            long bytes = 0;
            using var reader = command.ExecuteReader();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new CollectionChangePage(changes, HasMore: false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                // The extra row proves continuation without copying its id or body into managed memory.
                if (changes.Count == limit)
                {
                    return new CollectionChangePage(changes, HasMore: true);
                }

                var idBytes = reader.GetInt64(5);
                if (idBytes is 0 or > MaxIdBytes)
                {
                    throw new InvalidDataException("a stored collection change has an oversized or empty id");
                }

                var size = 2048L + idBytes + reader.GetInt64(6);
                if (size > maxBytes - bytes)
                {
                    if (changes.Count == 0)
                    {
                        throw new InvalidDataException("a stored collection change exceeds the page byte limit");
                    }

                    return new CollectionChangePage(changes, HasMore: true);
                }

                if (reader.IsDBNull(4))
                {
                    ReserveJson(0);
                }

                var offset = reader.GetInt64(0);
                var deleted = reader.IsDBNull(4);
                var document = new StoredDocument(reader.GetString(2), reader.GetInt64(3),
                    deleted ? ReadOnlyMemory<byte>.Empty : ReadShapeJson(reader, 4, reader.GetInt64(6)), Scope, Deleted: deleted, HistoryOffset: offset);
                changes.Add(new CollectionChange(offset, reader.GetInt64(1), document));
                bytes += size;
            }
        }, cancellationToken), cancellationToken);
    }
}
