namespace TerraBase.Engine;

/// <summary>One stored JSON record: its id, the commit offset of the transaction that last wrote it, and its body.</summary>
public sealed record StoredDocument(string Id, long Version, ReadOnlyMemory<byte> Json);

public sealed record DocumentPage(IReadOnlyList<StoredDocument> Documents, bool HasMore);

public sealed record CollectionInfo(string Name, long CreatedAt, string Id = "");

/// <summary>
/// One transaction on a database. Records are plain rows keyed by collection and id: a write replaces the row and stamps it
/// with the transaction's commit offset, a delete removes it. Client databases also run customer SQL through it.
/// </summary>
public interface IStorageTransaction : IAsyncDisposable
{
    ValueTask<Ulid> GetIncarnationAsync(CancellationToken cancellationToken);

    ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>Bytes of all stored record bodies.</summary>
    ValueTask<long> GetStoredDocumentBytesAsync(CancellationToken cancellationToken);

    /// <summary>The record's version, or <see langword="null"/> when it does not exist.</summary>
    ValueTask<long?> GetVersionAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>Writes a record, creating its collection when missing, and returns its new version.</summary>
    ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken);

    /// <summary>Deletes a record; false when it did not exist.</summary>
    ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken);

    ValueTask<DocumentPage> ListAsync(string collection, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken);

    ValueTask<long> GetOffsetAsync(CancellationToken cancellationToken);

    ValueTask<bool> CreateCollectionAsync(CollectionInfo collection, CancellationToken cancellationToken);

    ValueTask<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken);

    ValueTask<(IReadOnlyList<CollectionInfo> Collections, bool HasMore)> ListCollectionsAsync(
    string? afterName, int limit, CancellationToken cancellationToken);

    /// <summary>Drops a collection and its records; false when it did not exist.</summary>
    ValueTask<bool> DropCollectionAsync(string name, CancellationToken cancellationToken);

    /// <summary>Drops every collection whose name starts with <paramref name="prefix"/>, returning the records removed.</summary>
    ValueTask<long> DeleteCollectionsAsync(string prefix, CancellationToken cancellationToken);

    internal ValueTask<SqlStatementResult> ExecuteStatementAsync(SqlStatement statement, CancellationToken cancellationToken);

    internal ValueTask<SqlStatementShape> DescribeStatementAsync(string sql, CancellationToken cancellationToken);

    internal void InterruptStatement();

    internal ValueTask<long> MeasureSqlStorageAsync(CancellationToken cancellationToken);

    internal ValueTask AddSqlStorageAsync(long delta, CancellationToken cancellationToken);

    ValueTask<CommitResult> CommitAsync(CancellationToken cancellationToken);
}
