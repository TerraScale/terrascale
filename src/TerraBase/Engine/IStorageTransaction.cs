namespace TerraBase.Engine;

public sealed record StoredRecord(string Id, long Version, ReadOnlyMemory<byte> Json);

public sealed record RecordPage(IReadOnlyList<StoredRecord> Records, bool HasMore);

public sealed record RecordKind(string Name, long CreatedAt, string Id = "");

public interface IStorageTransaction : IAsyncDisposable
{
    ValueTask<Ulid> GetIncarnationAsync(CancellationToken cancellationToken);

    ValueTask<StoredRecord?> GetAsync(string kind, string id, CancellationToken cancellationToken);

    ValueTask<long> GetStoredBytesAsync(CancellationToken cancellationToken);

    ValueTask<long?> GetVersionAsync(string kind, string id, CancellationToken cancellationToken);

    ValueTask<long> PutAsync(string kind, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken);

    ValueTask<bool> DeleteAsync(string kind, string id, CancellationToken cancellationToken);

    ValueTask<RecordPage> ListAsync(string kind, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken);

    ValueTask<long> GetOffsetAsync(CancellationToken cancellationToken);

    ValueTask<(IReadOnlyList<RecordKind> Kinds, bool HasMore)> ListKindsAsync(string? afterName, int limit, CancellationToken cancellationToken);

    ValueTask<bool> DropKindAsync(string name, CancellationToken cancellationToken);

    ValueTask<long> DeleteKindsAsync(string prefix, CancellationToken cancellationToken);

    internal ValueTask<SqlStatementResult> ExecuteStatementAsync(SqlStatement statement, CancellationToken cancellationToken);

    internal ValueTask<SqlStatementShape> DescribeStatementAsync(string sql, CancellationToken cancellationToken);

    internal void InterruptStatement();

    internal ValueTask SuspendForeignKeysAsync(CancellationToken cancellationToken);

    internal ValueTask<IReadOnlyList<CapturedRowChange>> ReadCapturedChangesAsync(CancellationToken cancellationToken);

    internal ValueTask<long> MeasureSqlStorageAsync(CancellationToken cancellationToken);

    internal ValueTask AddSqlStorageAsync(long delta, CancellationToken cancellationToken);

    ValueTask<CommitResult> CommitAsync(CancellationToken cancellationToken);
}
