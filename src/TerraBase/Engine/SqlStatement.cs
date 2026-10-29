namespace TerraBase.Engine;

internal sealed record SqlStatement(
    string Sql, IReadOnlyList<object?> Args, IReadOnlyDictionary<string, object?> Named, int MaxRows, long MaxBytes, TimeSpan Timeout, bool First = false);

internal sealed record SqlStatementColumn(string Name, string? DeclType);

internal sealed record SqlStatementResult(
    IReadOnlyList<SqlStatementColumn> Columns, IReadOnlyList<object?[]> Rows, long RowsAffected, long LastInsertRowid, long GrowthBytes,
    bool Truncated, int Consumed);

internal sealed record SqlStatementShape(IReadOnlyList<string?> Parameters, IReadOnlyList<SqlStatementColumn> Columns);

internal enum SqlFailure
{
    Error,
    Syntax,
    NoSuchTable,
    NoSuchColumn,
    AlreadyExists,
    PrimaryKey,
    Unique,
    NotNull,
    Check,
    ForeignKey,
    Constraint,
    Busy,
    TooBig,
    Interrupted,
    ReadOnly,
}

internal sealed class SqlStatementException(SqlFailure failure, string message, bool transactionEnded, Exception? innerException = null)
    : Exception(message, innerException)
{
    public SqlFailure Failure { get; } = failure;

    public bool TransactionEnded { get; } = transactionEnded;
}
