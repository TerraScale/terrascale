namespace TerraBase.Engine;

/// <summary>One system record: its id, the commit offset of the transaction that last wrote it, and its JSON body.</summary>
public sealed record StoredRecord(string Id, long Version, ReadOnlyMemory<byte> Json);

public sealed record RecordPage(IReadOnlyList<StoredRecord> Records, bool HasMore);

/// <summary>A named set of system records, such as the API keys or one bucket's files.</summary>
public sealed record RecordKind(string Name, long CreatedAt, string Id = "");

/// <summary>
/// One transaction on a database. Client databases run customer SQL through it. The system database also keeps
/// TerraBase's own records as plain rows keyed by kind and id: a write replaces the row and stamps it with the
/// transaction's commit offset, a delete removes it. Record operations on a client database throw
/// <see cref="InvalidOperationException"/>.
/// </summary>
public interface IStorageTransaction : IAsyncDisposable
{
    ValueTask<Ulid> GetIncarnationAsync(CancellationToken cancellationToken);

    ValueTask<StoredRecord?> GetAsync(string kind, string id, CancellationToken cancellationToken);

    /// <summary>Bytes the database's data holds: its records in the system database, its SQL tables in a client database.</summary>
    ValueTask<long> GetStoredBytesAsync(CancellationToken cancellationToken);

    /// <summary>The record's version, or <see langword="null"/> when it does not exist.</summary>
    ValueTask<long?> GetVersionAsync(string kind, string id, CancellationToken cancellationToken);

    /// <summary>Writes a record, creating its kind when missing, and returns its new version.</summary>
    ValueTask<long> PutAsync(string kind, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken);

    /// <summary>Deletes a record; false when it did not exist.</summary>
    ValueTask<bool> DeleteAsync(string kind, string id, CancellationToken cancellationToken);

    ValueTask<RecordPage> ListAsync(string kind, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken);

    ValueTask<long> GetOffsetAsync(CancellationToken cancellationToken);

    ValueTask<(IReadOnlyList<RecordKind> Kinds, bool HasMore)> ListKindsAsync(string? afterName, int limit, CancellationToken cancellationToken);

    /// <summary>Drops a kind and its records; false when it did not exist.</summary>
    ValueTask<bool> DropKindAsync(string name, CancellationToken cancellationToken);

    /// <summary>Drops every kind whose name starts with <paramref name="prefix"/>, returning the records removed.</summary>
    ValueTask<long> DeleteKindsAsync(string prefix, CancellationToken cancellationToken);

    internal ValueTask<SqlStatementResult> ExecuteStatementAsync(SqlStatement statement, CancellationToken cancellationToken);

    internal ValueTask<SqlStatementShape> DescribeStatementAsync(string sql, CancellationToken cancellationToken);

    internal void InterruptStatement();

    internal ValueTask<long> MeasureSqlStorageAsync(CancellationToken cancellationToken);

    internal ValueTask AddSqlStorageAsync(long delta, CancellationToken cancellationToken);

    ValueTask<CommitResult> CommitAsync(CancellationToken cancellationToken);
}
