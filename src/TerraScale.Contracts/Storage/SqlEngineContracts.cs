using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Continuity;

namespace TerraScale.Contracts.Storage;

public enum SqlValueKind
{
    Null,
    Integral,
    Real,
    Text,
    Blob,
}

public readonly struct SqlValue : IEquatable<SqlValue>
{
    private readonly long _integral;
    private readonly double _real;
    private readonly string? _text;
    private readonly ReadOnlyMemory<byte> _blob;

    private SqlValue(SqlValueKind kind, long integral, double real, string? text, ReadOnlyMemory<byte> blob)
    {
        Kind = kind;
        _integral = integral;
        _real = real;
        _text = text;
        _blob = blob;
    }

    public SqlValueKind Kind { get; }

    public static SqlValue Null => default;

    public static SqlValue FromIntegral(long value) => new(SqlValueKind.Integral, value, 0, null, default);

    public static SqlValue FromReal(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "SQL real values must be finite.");
        }

        return new(SqlValueKind.Real, 0, value, null, default);
    }

    public static SqlValue FromText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlValueKind.Text, 0, 0, value, default);
    }

    public static SqlValue FromBlob(ReadOnlyMemory<byte> value) => new(SqlValueKind.Blob, 0, 0, null, value);

    public bool TryGetIntegral(out long value)
    {
        value = _integral;
        return Kind == SqlValueKind.Integral;
    }

    public bool TryGetReal(out double value)
    {
        value = _real;
        return Kind == SqlValueKind.Real;
    }

    public bool TryGetText([NotNullWhen(true)] out string? value)
    {
        value = _text;
        return Kind == SqlValueKind.Text && value is not null;
    }

    public bool TryGetBlob(out ReadOnlyMemory<byte> value)
    {
        value = _blob;
        return Kind == SqlValueKind.Blob;
    }

    public bool Equals(SqlValue other) => Kind == other.Kind && Kind switch
    {
        SqlValueKind.Null => true,
        SqlValueKind.Integral => _integral == other._integral,
        SqlValueKind.Real => _real.Equals(other._real),
        SqlValueKind.Text => string.Equals(_text, other._text, StringComparison.Ordinal),
        SqlValueKind.Blob => _blob.Span.SequenceEqual(other._blob.Span),
        _ => throw new UnreachableException(),
    };

    public override bool Equals(object? obj) => obj is SqlValue other && Equals(other);

    public override int GetHashCode() => Kind switch
    {
        SqlValueKind.Null => 0,
        SqlValueKind.Integral => HashCode.Combine(Kind, _integral),
        SqlValueKind.Real => HashCode.Combine(Kind, _real),
        SqlValueKind.Text => HashCode.Combine(Kind, string.GetHashCode(_text.AsSpan(), StringComparison.Ordinal)),
        SqlValueKind.Blob => BlobHash(_blob.Span),
        _ => throw new UnreachableException(),
    };

    public static bool operator ==(SqlValue left, SqlValue right) => left.Equals(right);

    public static bool operator !=(SqlValue left, SqlValue right) => !left.Equals(right);

    private static int BlobHash(ReadOnlySpan<byte> blob)
    {
        var hash = new HashCode();
        hash.Add(SqlValueKind.Blob);
        hash.AddBytes(blob);
        return hash.ToHashCode();
    }
}

public enum SqlAccess
{
    Read,
    Write,
}

public sealed record SqlStatement(string Sql, IReadOnlyList<SqlValue> Positional, IReadOnlyDictionary<string, SqlValue> Named);

public sealed record SqlStatementRequest(
    Ulid DatabaseId,
    IReadOnlyList<SqlStatement> Statements,
    SqlAccess Access,
    ConsistencyMode Mode,
    CausalBookmark After);

public sealed record SqlColumn(string Name, string? DeclaredType);

public sealed record SqlResultSet(
    IReadOnlyList<SqlColumn> Columns,
    IReadOnlyList<IReadOnlyList<SqlValue>> Rows,
    long RowsAffected,
    long LastInsertRowId);

public sealed record SqlReadResult(IReadOnlyList<SqlResultSet> ResultSets, CausalBookmark Observed);

public sealed record SqlWriteResult(IReadOnlyList<SqlResultSet> ResultSets, CommitMetadata Commit);

public sealed record SqlError(string Code, string Message, string SqlState, int StatementIndex, int SqliteExtendedCode);

public sealed record StorageBusy(string Message, TimeSpan RetryAfter);

public sealed record StorageCorrupted(Ulid DatabaseId, string Message);

public sealed record SchemaMismatch(Ulid DatabaseId, ulong ExpectedEpoch, ulong ActualEpoch);

public sealed record ReadSetConflict(Ulid DatabaseId, IReadOnlyList<RowKey> StaleKeys, IReadOnlyList<string> StaleTables);

public sealed record StrongRequired(Ulid DatabaseId, StrongRequiredReason Reason);

public sealed record CommitLogGap(Ulid DatabaseId, string OriginRegion, long RequestedFrom, long OldestAvailable);

public enum StrongRequiredReason
{
    SchemaChange,
    SecondaryUniqueIndex,
    StrictTable,
}

public sealed record RowKey(string Table, string PrimaryKey);

public sealed record ReadKey(RowKey Key, HlcTimestamp Observed);

public readonly record struct StagedWriteToken(Ulid Value);

public sealed record StagedWrite(
    StagedWriteToken Token,
    Ulid DatabaseId,
    Ulid TxId,
    HlcTimestamp ReadSnapshot,
    ulong SchemaEpoch,
    IReadOnlyList<ReadKey> ReadSet,
    IReadOnlyList<string> ScannedTables,
    IReadOnlyList<RowKey> WriteKeys,
    bool IsSchemaChange,
    IReadOnlyList<SqlResultSet> ResultSets,
    ReadOnlyMemory<byte> DeltaPayload);

public sealed record CommitMetadata(
    Ulid TxId,
    string OriginRegion,
    long Sequence,
    HlcTimestamp Hlc,
    ulong SchemaEpoch,
    ConsistencyMode Mode,
    CausalBookmark Bookmark,
    ReadOnlyMemory<byte> DeltaPayload);

public sealed record RemoteCommitBatch(Ulid DatabaseId, IReadOnlyList<CommitMetadata> Commits);

public sealed record RemoteApplyOutcome(int Applied, int Duplicates, int Buffered, CausalBookmark Bookmark);

public sealed record CommitLogPage(Ulid DatabaseId, string OriginRegion, IReadOnlyList<CommitMetadata> Entries, long NextSequence);

public sealed record CommitLogSegment(
    Ulid DatabaseId,
    string OriginRegion,
    long FirstSequence,
    long LastSequence,
    long SizeBytes,
    DateTimeOffset SealedAtUtc);

public sealed record DatabaseOpenRequest(Ulid DatabaseId, ReadOnlyMemory<byte> RawKey)
{
    public const int RawKeyLength = 32;
}

public sealed record DatabaseCreateRequest(Ulid DatabaseId, ReadOnlyMemory<byte> RawKey, bool Strict);

public sealed record DatabaseBranchRequest(
    Ulid SourceDatabaseId,
    Ulid BranchDatabaseId,
    ReadOnlyMemory<byte> BranchRawKey,
    CausalBookmark At);

public sealed record DatabaseSnapshot(
    Ulid DatabaseId,
    CausalBookmark Bookmark,
    ulong SchemaEpoch,
    long SizeBytes,
    DateTimeOffset TakenAtUtc,
    string Path);

public sealed record SqlTransactionOptions(ConsistencyMode Mode, SqlAccess Access, CausalBookmark After);

public interface ISqlTransaction : IAsyncDisposable
{
    Ulid TxId { get; }

    SqlTransactionOptions Options { get; }

    ValueTask<Result<SqlResultSet, SqlError, StorageBusy, RequestTimeout>> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken);

    ValueTask<Result<CommitMetadata, StrongRequired, SchemaMismatch, StorageBusy, RequestTimeout>> CommitLocalAsync(CancellationToken cancellationToken);

    ValueTask<Result<StagedWrite, SchemaMismatch, StorageBusy, RequestTimeout>> StageAsync(CancellationToken cancellationToken);

    ValueTask<Done> RollbackAsync(CancellationToken cancellationToken);
}

public interface ISqlDatabaseSession : IAsyncDisposable
{
    Ulid DatabaseId { get; }

    ulong SchemaEpoch { get; }

    CausalBookmark CurrentBookmark { get; }

    ValueTask<Result<SqlReadResult, SqlError, StorageBusy, RequestTimeout>> ExecuteReadAsync(SqlStatementRequest request, CancellationToken cancellationToken);

    ValueTask<Result<SqlWriteResult, SqlError, StrongRequired, SchemaMismatch, StorageBusy, RequestTimeout>> ExecuteWriteAsync(SqlStatementRequest request, CancellationToken cancellationToken);

    ValueTask<Result<StagedWrite, SqlError, SchemaMismatch, StorageBusy, RequestTimeout>> StageWriteAsync(SqlStatementRequest request, CancellationToken cancellationToken);

    ValueTask<Result<ISqlTransaction, StorageBusy, RequestTimeout>> BeginTransactionAsync(SqlTransactionOptions options, CancellationToken cancellationToken);

    ValueTask<Result<Done, ReadSetConflict, SchemaMismatch, StorageBusy>> ValidateReadSetAsync(
        HlcTimestamp readSnapshot,
        IReadOnlyList<ReadKey> readSet,
        IReadOnlyList<string> scannedTables,
        ulong schemaEpoch,
        CancellationToken cancellationToken);

    ValueTask<Result<CommitMetadata, NotFound, ReadSetConflict, SchemaMismatch, StorageBusy>> CommitStagedAsync(StagedWriteToken token, HlcTimestamp commitHlc, CancellationToken cancellationToken);

    ValueTask<Result<Done, NotFound>> AbortStagedAsync(StagedWriteToken token, CancellationToken cancellationToken);

    ValueTask<Result<RemoteApplyOutcome, SchemaMismatch, StorageCorrupted, StorageBusy>> ApplyRemoteBatchAsync(RemoteCommitBatch batch, CancellationToken cancellationToken);

    ValueTask<Result<CommitLogPage, CommitLogGap, StorageBusy>> ReadCommitLogAsync(string originRegion, long fromSequence, int maxEntries, CancellationToken cancellationToken);

    ValueTask<Result<IReadOnlyList<CommitLogSegment>, StorageBusy>> ListSealedSegmentsAsync(string originRegion, long afterSequence, CancellationToken cancellationToken);

    ValueTask<Result<Stream, NotFound, StorageBusy>> OpenSegmentAsync(CommitLogSegment segment, CancellationToken cancellationToken);

    ValueTask<Result<Done, Validation, StorageCorrupted, StorageBusy>> ImportSegmentAsync(CommitLogSegment segment, Stream content, CancellationToken cancellationToken);

    ValueTask<Result<Done, RequestTimeout>> WaitForBookmarkAsync(CausalBookmark bookmark, TimeSpan timeout, CancellationToken cancellationToken);

    IAsyncEnumerable<ChangefeedEntry> SubscribeChangesAsync(CausalBookmark after, CancellationToken cancellationToken);
}

public interface ISqlStorageEngine : IAsyncDisposable
{
    ValueTask<Result<ISqlDatabaseSession, NotFound, Validation, StorageCorrupted, StorageBusy>> OpenAsync(DatabaseOpenRequest request, CancellationToken cancellationToken);

    ValueTask<Result<ISqlDatabaseSession, Conflict, Validation, StorageBusy>> CreateAsync(DatabaseCreateRequest request, CancellationToken cancellationToken);

    ValueTask<Result<ISqlDatabaseSession, NotFound, Conflict, Validation, StorageBusy>> BranchAsync(DatabaseBranchRequest request, CancellationToken cancellationToken);

    ValueTask<Result<DatabaseSnapshot, NotFound, StorageBusy>> SnapshotAsync(Ulid databaseId, CancellationToken cancellationToken);

    ValueTask<Result<Done, NotFound, StorageBusy>> DeleteAsync(Ulid databaseId, CancellationToken cancellationToken);

    ValueTask<Result<Done, NotFound, StorageBusy>> CryptoShredAsync(Ulid databaseId, CancellationToken cancellationToken);
}
