using TerraBase.Engine;
using TerraBase.Tenancy;
using TerraBase.Features.Sql.Routing;

namespace TerraBase.Features.Sql;

internal enum SqlValueKind
{
    Null,
    Integer,
    Real,
    Text,
    Blob,
}

internal readonly record struct SqlValue(SqlValueKind Kind, long Integer = 0, double Real = 0, string? Text = null, byte[]? Blob = null)
{
    public static SqlValue Null => default;

    public static SqlValue Of(long value) => new(SqlValueKind.Integer, Integer: value);

    public static SqlValue Of(double value) => new(SqlValueKind.Real, Real: value);

    public static SqlValue Of(string value) => new(SqlValueKind.Text, Text: value);

    public static SqlValue Of(byte[] value) => new(SqlValueKind.Blob, Blob: value);
}

internal sealed record SqlCommand(string Sql, IReadOnlyList<SqlValue> Args, IReadOnlyDictionary<string, SqlValue> Named);

internal sealed record SqlPlannedCommand(SqlStatementPlan Plan, SqlCommand Command);

internal sealed record SqlColumn(string Name, string? DeclType);

internal sealed record SqlResult(IReadOnlyList<SqlColumn> Columns, IReadOnlyList<IReadOnlyList<SqlValue>> Rows, long RowsAffected, long? LastInsertRowid)
{
    internal SqlReadCharge? ReadCharge { get; init; }
}

internal sealed class SqlReadCharge(Action accept)
{
    private Action? _accept = accept;

    public void Accept() => Interlocked.Exchange(ref _accept, null)?.Invoke();
}

internal sealed record SqlError(string Code, string Message, string? Home = null, int? RetryAfterSeconds = null)
{
    public string Sqlstate => SqlErrors.Sqlstate(Code);

    public int Status => SqlErrors.Status(Code);
}

internal sealed record SqlDescription(IReadOnlyList<string?> Params, IReadOnlyList<SqlColumn> Columns, bool IsExplain, bool IsReadOnly);

internal readonly union SqlOutcome(SqlResult, SqlError);

internal readonly union SqlDescribeOutcome(SqlDescription, SqlError);

internal interface ISqlSession : IAsyncDisposable
{
    bool IsAutocommit { get; }

    TimeSpan? TransactionRemaining { get; }

    SqlTransactionDeadline TransactionDeadline { get; set; }

    void BeginRequest();

    ValueTask<SqlOutcome> ExecuteAsync(SqlPlannedCommand planned, CancellationToken cancellationToken);

    ValueTask<SqlOutcome> ExecuteAsync(SqlStatementPlan plan, SqlCommand command, CancellationToken cancellationToken);

    ValueTask<SqlOutcome> ExecuteAsync(SqlStatementPlan plan, SqlCommand command, Func<SqlResult, SqlError?>? admit, CancellationToken cancellationToken);

    ValueTask<SqlBatchOutcome?> TryBatchAsync(IReadOnlyList<SqlPlannedCommand> commands, CancellationToken cancellationToken);

    ValueTask<SqlOutcome> ExecuteScriptAsync(string sql, CancellationToken cancellationToken);

    ValueTask<SqlOutcome> ExecuteScriptAsync(string sql, int maxStatements, CancellationToken cancellationToken);

    ValueTask<SqlDescribeOutcome> DescribeAsync(SqlStatementPlan plan, SqlCommand command, CancellationToken cancellationToken);

    ValueTask EndRequestAsync();

    void Prefer(WriteOverride preference);

    WriteRoute? LastRoute { get; }

    void KeepCommitted();

    IReadOnlyList<IReadOnlyList<CapturedRowChange>> Committed { get; }
}

internal sealed record SqlTableColumn(string Name, string? DeclType, bool NotNull, bool PrimaryKey, string? Default = null);

internal sealed record SqlTableInfo(string Name, IReadOnlyList<SqlTableColumn> Columns);

internal sealed record ReplicaSqlFailure(SqlError Error, int Statement);

internal readonly union ReplicaSqlOutcome(IReadOnlyList<SqlResult>, ReplicaSqlFailure);

internal interface ISqlService
{
    ValueTask<ReplicaSqlOutcome> ExecuteOnReplicaAsync(
        TenantContext database, IReplicaReader reader, IReadOnlyList<SqlPlannedCommand> statements, CancellationToken cancellationToken);

    ValueTask<ReplicaSqlOutcome> ExecuteOnCopyAsync(
        TenantContext database, Func<SqlStatement, ValueTask<SqlStatementResult>> run, IReadOnlyList<SqlPlannedCommand> statements,
        bool write, CancellationToken cancellationToken, Action<string, double>? timing = null);

    void RecordLocalWrites(TenantContext database, long rows);

    ValueTask<IReadOnlyList<SqlTableInfo>> ListTablesAsync(TenantContext database, CancellationToken cancellationToken);

    ISqlSession Open(TenantContext database, bool canWrite);
}
