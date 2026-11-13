namespace TerraBase.Engine;

internal sealed record SqlStatement(
    string Sql, IReadOnlyList<object?> Args, IReadOnlyDictionary<string, object?> Named, int MaxRows, long MaxBytes, TimeSpan Timeout, bool First = false);

internal sealed record SqlStatementColumn(string Name, string? DeclType);

internal sealed record SqlStatementResult(
    IReadOnlyList<SqlStatementColumn> Columns, IReadOnlyList<object?[]> Rows, long RowsAffected, long LastInsertRowid, long GrowthBytes,
    bool Truncated, int Consumed, TimeSpan Execution = default);

internal sealed record CapturedRowChange(
    string Table, int Type, IReadOnlyList<string> Columns, object?[]? Before, object?[]? After, bool[]? Updated, CapturedImages? Images = null)
{
    public const int Insert = 1;

    public const int Update = 0;

    public const int Delete = -1;
}

internal sealed record CapturedImages(long RowId, byte[]? Before, byte[]? After, long ChangeId = 0);

internal sealed record SqlStatementShape(IReadOnlyList<string?> Parameters, IReadOnlyList<SqlStatementColumn> Columns);

internal closed record class SqlFailure;

internal sealed record class Error : SqlFailure;

internal sealed record class KnownError : SqlFailure;

internal sealed record class StorageFailure : SqlFailure;

internal sealed record class Syntax : SqlFailure;

internal sealed record class NoSuchTable : SqlFailure;

internal sealed record class NoSuchColumn : SqlFailure;

internal sealed record class AlreadyExists : SqlFailure;

internal sealed record class PrimaryKey : SqlFailure;

internal sealed record class Unique : SqlFailure;

internal sealed record class NotNull : SqlFailure;

internal sealed record class Check : SqlFailure;

internal sealed record class ForeignKeyFailure : SqlFailure;

internal sealed record class Constraint : SqlFailure;

internal sealed record class Busy : SqlFailure;

internal sealed record class TooBig : SqlFailure;

internal sealed record class Interrupted : SqlFailure;

internal sealed record class ReadOnly : SqlFailure;

internal sealed record class StaleRow : SqlFailure;

internal sealed class SqlStatementException(SqlFailure failure, string message, bool transactionEnded, Exception? innerException = null)
    : Exception(message, innerException)
{
    public SqlFailure Failure { get; } = failure;

    public bool TransactionEnded { get; } = transactionEnded;
}
