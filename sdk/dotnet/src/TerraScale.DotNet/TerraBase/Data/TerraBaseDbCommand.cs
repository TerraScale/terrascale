using System.Diagnostics.CodeAnalysis;
using System.Data;
using System.Data.Common;

namespace TerraScale.DotNet.TerraBase;

/// <summary>One ADO.NET command: autocommit statement execution over SQL REST.</summary>
public sealed class TerraBaseDbCommand : DbCommand
{
    private readonly TerraBaseDbParameterCollection parameters = [];
    private TerraBaseDbConnection? connection;

    /// <summary>Creates an unbound command.</summary>
    public TerraBaseDbCommand()
    {
    }

    /// <summary>Creates a command bound to <paramref name="connection"/>.</summary>
    public TerraBaseDbCommand(TerraBaseDbConnection connection) => this.connection = connection;

    /// <summary>The statement text.</summary>
    [AllowNull]
    public override string CommandText
    {
        get;

        set => field = value ?? "";
    } = "";

    /// <summary>The command timeout; execution deadlines are enforced by the SDK.</summary>
    public override int CommandTimeout { get; set; }

    /// <summary>Always <see cref="System.Data.CommandType.Text"/>.</summary>
    public override CommandType CommandType
    {
        get => CommandType.Text;
        set => ArgumentOutOfRangeException.ThrowIfNotEqual(value, CommandType.Text, nameof(value));
    }

    /// <summary>Always false.</summary>
    public override bool DesignTimeVisible { get; set; }

    /// <summary>Always <see cref="System.Data.UpdateRowSource.None"/>.</summary>
    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;

    /// <summary>The connection this command runs on.</summary>
    protected override DbConnection? DbConnection
    {
        get => connection;
        set => connection = value is TerraBaseDbConnection bound ? bound
            : value is null ? null
            : throw new ArgumentException("The connection must be a TerraBaseDbConnection.", nameof(value));
    }

    /// <summary>The transaction the command runs in; while one is open on the connection, it must be that transaction.</summary>
    protected override DbTransaction? DbTransaction { get; set; }

    /// <summary>The command's parameters.</summary>
    protected override DbParameterCollection DbParameterCollection => parameters;

    /// <summary>Does nothing; statements are not prepared.</summary>
    public override void Prepare()
    {
    }

    /// <summary>Execution cannot be cancelled after it started; pass a <see cref="CancellationToken"/> instead.</summary>
    public override void Cancel()
    {
    }

    /// <summary>Creates an empty parameter.</summary>
    protected override DbParameter CreateDbParameter() => new TerraBaseDbParameter();

    /// <summary>The number of rows a write changed.</summary>
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
        (int)(await SendAsync(cancellationToken).ConfigureAwait(false)).RowsAffected;

    /// <summary>The first column of the first row, or null when the result has no rows.</summary>
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var result = await SendAsync(cancellationToken).ConfigureAwait(false);
        return result.Rows.Count > 0 ? TerraBaseDbDataReader.Box(result.Rows[0][0]) : null;
    }

    /// <summary>A reader over the statement's rows.</summary>
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var result = await SendAsync(cancellationToken).ConfigureAwait(false);
        return new TerraBaseDbDataReader(result, behavior.HasFlag(CommandBehavior.SingleRow));
    }

    /// <summary>The SDK is async-only; use <see cref="ExecuteNonQueryAsync(CancellationToken)"/>.</summary>
    public override int ExecuteNonQuery() => throw NotSync();

    /// <summary>The SDK is async-only; use <see cref="ExecuteScalarAsync(CancellationToken)"/>.</summary>
    public override object? ExecuteScalar() => throw NotSync();

    /// <summary>The SDK is async-only; use <see cref="ExecuteDbDataReaderAsync(CommandBehavior, CancellationToken)"/>.</summary>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw NotSync();

    private static NotSupportedException NotSync() => new("The TerraScale SDK is async-only; use the async method with a CancellationToken.");

    private async Task<TerraBaseSqlStatementResult> SendAsync(CancellationToken cancellationToken)
    {
        var connection = this.connection ?? throw new InvalidOperationException("The command has no TerraBaseDbConnection.");
        ArgumentException.ThrowIfNullOrWhiteSpace(CommandText, nameof(CommandText));
        var (args, named) = parameters.ToArguments();
        var statement = new TerraBaseSqlStatement(CommandText, args, named);
        var open = connection.CurrentTransaction;
        var enlisted = DbTransaction as TerraBaseDbTransaction;
        if (open != enlisted)
        {
            throw new InvalidOperationException(open is null
                ? "The command's transaction is not active on this connection."
                : "The connection has an open transaction; set the command's Transaction property to it.");
        }

        return open is { } transaction
            ? await transaction.Session.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false)
            : await connection.SqlDatabase.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
    }
}
