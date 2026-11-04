using System.Buffers;
using System.Text;
using System.Text.Json;
using TerraBase.Features.Qos;
using TerraBase.Primitives;

namespace TerraBase.Engine.Turso;

/// <summary>
/// Records are plain rows of <c>_tb_documents</c>, one per collection and id, holding the JSON body and the commit offset of
/// the transaction that last wrote it as its version. A delete removes the row; nothing else is kept. Collections are rows
/// of <c>_tb_collections</c>.
/// </summary>
internal sealed partial class TursoStorageTransaction
{
    private const long MaxWrittenBytes = 7 * 1024 * 1024;
    private const int MaxWrites = 1024;
    private const int MaxIdBytes = 1024;
    private const int OpFramingBytes = 128;

    private long _changeBytes;
    private int _captured;
    private int _writes;
    private long _writtenBytes;

    // Collections a write transaction found: only it can drop one (it holds the writer), so a found collection stays until
    // this transaction drops a collection or rolls back to a savepoint, which forget them all.
    private HashSet<string>? _tables;

    public ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var command = Command(TursoStatements.Get, collection);
            command.Parameters.AddWithValue("$id", id);
            CountRead("point");
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            ReserveJson(reader.GetInt64(2));
            return new StoredDocument(id, reader.GetInt64(0), TursoSession.Bytes(reader, 1));
        }, cancellationToken);

    public ValueTask<long> GetStoredDocumentBytesAsync(CancellationToken cancellationToken) =>
        RunAsync(() => (long)Command(TursoStatements.StoredDocumentBytes).ExecuteScalar()!
            + (IsClient ? (long)Command(TursoStatements.SqlStorageBytes).ExecuteScalar()! : 0), cancellationToken);

    public ValueTask<long?> GetVersionAsync(string collection, string id, CancellationToken cancellationToken) =>
        RunAsync(() => ReadVersion(collection, id), cancellationToken);

    private long? ReadVersion(string collection, string id)
    {
        var command = Command(TursoStatements.Version, collection);
        command.Parameters.AddWithValue("$id", id);
        CountRead("point");
        return command.ExecuteScalar() as long?;
    }

    public ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ThrowIfReadOnly();
            var body = DocumentJson.Read(json, requireObject: IsClient);
            Reserve(collection, id, json.Length);
            if (!HasCollection(collection))
            {
                _ = CreateCollection(new CollectionInfo(collection, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var version = Offset();
            var command = Command(TursoStatements.Put, collection);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$version", version);
            command.Parameters.AddWithValue("$body", body);
            command.ExecuteNonQuery();
            return version;
        }, cancellationToken, mutation: true);

    public ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ThrowIfReadOnly();
            if (ReadVersion(collection, id) is null)
            {
                return false;
            }

            Reserve(collection, id, 0);
            cancellationToken.ThrowIfCancellationRequested();
            _ = Offset();
            var command = Command(TursoStatements.Delete, collection);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            return true;
        }, cancellationToken, mutation: true);

    private void Reserve(string collection, string id, int documentBytes)
    {
        if (id.Length == 0)
        {
            throw new InvalidDataException("a document id is empty");
        }

        if (!IsClient)
        {
            return;
        }

        var bytes = documentBytes + (long)JsonEncodedText.Encode(id).EncodedUtf8Bytes.Length + OpFramingBytes;
        if (Encoding.UTF8.GetByteCount(id) > MaxIdBytes || _writes == MaxWrites || _writtenBytes + bytes > MaxWrittenBytes)
        {
            LimitsReached = _writes > 0;
            throw new StorageWriteLimitException(
                "a transaction writes at most 1024 documents of at most 1 MiB, 7 MiB in total with framing, with ids of 1 to 1024 bytes");
        }

        _writes++;
        _writtenBytes += bytes;
        _captured++;
        _changeBytes += bytes + Encoding.UTF8.GetByteCount(collection);
    }

    public ValueTask<DocumentPage> ListAsync(
        string collection, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken) =>
        RunAsync(() => WithQueryBudget(() =>
        {
            var command = Command(afterId is null ? TursoStatements.ListFirst : TursoStatements.ListAfter, collection);
            if (afterId is not null)
            {
                command.Parameters.AddWithValue("$after", afterId);
            }

            command.Parameters.AddWithValue("$limit", limit + 1);
            var documents = new List<StoredDocument>();
            long bytes = 0;
            CountRead("query");
            using var reader = command.ExecuteReader();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.Read())
                {
                    break;
                }

                var size = reader.GetInt64(3);
                if (documents.Count == limit || (documents.Count > 0 && bytes + size > maxBytes))
                {
                    return new DocumentPage(documents, HasMore: true);
                }

                bytes += size;
                ReserveJson(size);
                documents.Add(new StoredDocument(reader.GetString(0), reader.GetInt64(1), TursoSession.Bytes(reader, 2)));
            }

            return new DocumentPage(documents, HasMore: false);
        }, cancellationToken), cancellationToken);

    public ValueTask<bool> CreateCollectionAsync(CollectionInfo collection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return RunAsync(() =>
        {
            ThrowIfReadOnly();
            return CreateCollection(collection);
        }, cancellationToken, mutation: true);
    }

    private bool CreateCollection(CollectionInfo collection)
    {
        collection = collection with { Id = collection.Id.Length == 0 ? EntityIds.New().ToString() : collection.Id };
        if (!EntityIds.IsValid(collection.Id))
        {
            throw new ArgumentException("a collection id must be a canonical uppercase ULID", nameof(collection));
        }

        var command = Command(TursoStatements.CreateCollection, collection.Name);
        command.Parameters.AddWithValue("$created", collection.CreatedAt);
        command.Parameters.AddWithValue("$entityId", collection.Id);
        if (command.ExecuteNonQuery() == 0)
        {
            return false;
        }

        _ = Offset();
        ChangedReaderState = true;
        (_tables ??= [with(StringComparer.Ordinal)]).Add(collection.Name);
        return true;
    }

    // Worker-only: whether the collection exists, from this transaction's finds or the store's loaded names when possible.
    private bool HasCollection(string collection)
    {
        if (_tables?.Contains(collection) == true)
        {
            return true;
        }

        if (!ChangedReaderState && store.TryKnownCollection(collection, out var known) && known is not null)
        {
            return true;
        }

        var exists = Command(TursoStatements.GetCollection, collection).ExecuteScalar() is not null;
        if (exists && write)
        {
            (_tables ??= [with(StringComparer.Ordinal)]).Add(collection);
        }

        return exists;
    }

    public ValueTask<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            using var reader = Command(TursoStatements.GetCollection, name).ExecuteReader();
            return reader.Read() ? new CollectionInfo(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)) : null;
        }, cancellationToken);

    public ValueTask<(IReadOnlyList<CollectionInfo> Collections, bool HasMore)> ListCollectionsAsync(
        string? afterName, int limit, CancellationToken cancellationToken) =>
        RunAsync<(IReadOnlyList<CollectionInfo>, bool)>(() =>
        {
            var command = Command(afterName is null ? TursoStatements.ListCollectionsFirst : TursoStatements.ListCollectionsAfter);
            if (afterName is not null)
            {
                command.Parameters.AddWithValue("$after", afterName);
            }

            command.Parameters.AddWithValue("$limit", limit + 1);
            var collections = new List<CollectionInfo>();
            using var reader = command.ExecuteReader();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.Read())
                {
                    break;
                }

                if (collections.Count == limit)
                {
                    return (collections, true);
                }

                collections.Add(new CollectionInfo(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
            }

            return (collections, false);
        }, cancellationToken);

    public ValueTask<bool> DropCollectionAsync(string name, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ThrowIfReadOnly();
            cancellationToken.ThrowIfCancellationRequested();
            var documents = Command(TursoStatements.DropDocuments, name).ExecuteNonQuery();
            _tables = null;
            if (Command(TursoStatements.DropCollection, name).ExecuteNonQuery() == 0 && documents == 0)
            {
                return false;
            }

            Captured(documents);
            _ = Offset();
            ChangedReaderState = true;
            return true;
        }, cancellationToken, mutation: true);

    public ValueTask<long> DeleteCollectionsAsync(string prefix, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ArgumentException.ThrowIfNullOrEmpty(prefix);
            ThrowIfReadOnly();
            cancellationToken.ThrowIfCancellationRequested();
            var through = PrefixSuccessor(prefix);
            var documents = Command(through is null ? TursoStatements.DeletePrefixDocumentsFrom : TursoStatements.DeletePrefixDocuments);
            var collections = Command(through is null ? TursoStatements.DeletePrefixCollectionsFrom : TursoStatements.DeletePrefixCollections);
            foreach (var command in new[] { documents, collections })
            {
                command.Parameters.AddWithValue("$from", prefix);
                if (through is not null)
                {
                    command.Parameters.AddWithValue("$to", through);
                }
            }

            long deleted = documents.ExecuteNonQuery();
            _tables = null;
            if (collections.ExecuteNonQuery() > 0 || deleted > 0)
            {
                Captured(deleted);
                _ = Offset();
                ChangedReaderState = true;
            }

            return deleted;
        }, cancellationToken, mutation: true);

    // A bulk delete of a client database is one replicated change of its own.
    private void Captured(long documents)
    {
        if (IsClient && documents > 0)
        {
            _captured++;
        }
    }

    private static string? PrefixSuccessor(string name)
    {
        var length = name.Length;
        while (length > 0)
        {
            if (Rune.DecodeLastFromUtf16(name.AsSpan(0, length), out var rune, out var consumed) != OperationStatus.Done)
            {
                throw new ArgumentException("Collection prefixes must contain valid Unicode scalar values.", nameof(name));
            }

            length -= consumed;
            if (rune.Value < 0x10ffff)
            {
                var next = rune.Value == 0xd7ff ? 0xe000 : rune.Value + 1;
                return name[..length] + new Rune(next).ToString();
            }
        }

        return null;
    }

    private void ReserveJson(long size) =>
        QosContext.ReserveResult(checked(size * 3 + 2048), store.IsSystem ? null : QosContext.Current);
}
