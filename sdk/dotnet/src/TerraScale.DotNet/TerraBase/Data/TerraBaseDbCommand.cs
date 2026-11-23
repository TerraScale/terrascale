using System.Diagnostics.CodeAnalysis;
using System.Data;
using System.Data.Common;

namespace TerraScale.DotNet.TerraBase;

public sealed class TerraBaseDbCommand : DbCommand
{
    private readonly TerraBaseDbParameterCollection parameters = [];
    private TerraBaseDbConnection? connection;

    public TerraBaseDbCommand()
    {
    }

    public TerraBaseDbCommand(TerraBaseDbConnection connection) => this.connection = connection;

    [AllowNull]
    public override string CommandText
    {
        get;

        set => field = string.IsNullOrEmpty(value) ? string.Empty : value;
    } = "";

    public override int CommandTimeout
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    }

    public override CommandType CommandType
    {
        get => CommandType.Text;
        set => ArgumentOutOfRangeException.ThrowIfNotEqual(value, CommandType.Text, nameof(value));
    }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;

    protected override DbConnection? DbConnection
    {
        get => connection;
        set => connection = value is TerraBaseDbConnection bound ? bound
            : value is null ? null
            : throw new ArgumentException("The connection must be a TerraBaseDbConnection.", nameof(value));
    }

    protected override DbTransaction? DbTransaction { get; set; }

    protected override DbParameterCollection DbParameterCollection => parameters;

    public override void Prepare()
    {
    }

    public override void Cancel()
    {
    }

    protected override DbParameter CreateDbParameter() => new TerraBaseDbParameter();

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var affected = 0L;
        foreach (var result in await SendAsync(cancellationToken).ConfigureAwait(false)) affected += result.RowsAffected;
        return (int)affected;
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var results = await SendAsync(cancellationToken).ConfigureAwait(false);
        return results.FirstOrDefault(result => result.Columns.Count > 0) is { Rows: [var first, ..] } ? first.Cell(0).Box() : null;
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
        new TerraBaseDbDataReader(await SendAsync(cancellationToken).ConfigureAwait(false), behavior.HasFlag(CommandBehavior.SingleRow));

    public override int ExecuteNonQuery() => throw NotSync();

    public override object? ExecuteScalar() => throw NotSync();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw NotSync();

    private static NotSupportedException NotSync() => new("The TerraScale SDK is async-only; use the async method with a CancellationToken.");

    private async Task<IReadOnlyList<TerraBaseSqlStatementResult>> SendAsync(CancellationToken cancellationToken)
    {
        var connection = this.connection ?? throw new InvalidOperationException("The command has no TerraBaseDbConnection.");
        ArgumentException.ThrowIfNullOrWhiteSpace(CommandText, nameof(CommandText));
        var statements = Statements();
        var open = connection.CurrentTransaction;
        var enlisted = DbTransaction as TerraBaseDbTransaction;
        if (open != enlisted)
        {
            throw new InvalidOperationException(open is null
                ? "The command's transaction is not active on this connection."
                : "The connection has an open transaction; set the command's Transaction property to it.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (CommandTimeout > 0) deadline.CancelAfter(TimeSpan.FromSeconds(CommandTimeout));
        try
        {
            return open is { } transaction
                ? await transaction.Session.BatchAsync(statements, deadline.Token).ConfigureAwait(false)
                : await connection.SqlDatabase.BatchAsync(statements, new TerraBaseSqlRequestOptions { Consistency = connection.Consistency }, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"The command did not finish within its CommandTimeout of {CommandTimeout} seconds.", error);
        }
    }

    private TerraBaseSqlStatement[] Statements()
    {
        var (args, named) = parameters.ToArguments();
        var script = TerraBaseSqlScript.Split(CommandText);
        if (script.Count <= 1) return [new TerraBaseSqlStatement(CommandText, args, named)];
        var statements = new TerraBaseSqlStatement[script.Count];
        var offset = 0;
        for (var i = 0; i < script.Count; i++)
        {
            var statement = script[i];
            if (statement.Numbered) throw new NotSupportedException("Numbered ?NNN parameters need a command with one statement; use named parameters.");
            IReadOnlyList<TerraBaseSqlValue>? slice = null;
            if (statement.Positional > 0)
            {
                if (args is null || offset + statement.Positional > args.Count)
                    throw new ArgumentException("The command's statements use more positional parameters than it has.", nameof(Parameters));
                slice = [.. args.Skip(offset).Take(statement.Positional)];
                offset += statement.Positional;
            }

            Dictionary<string, TerraBaseSqlValue>? used = null;
            if (named is not null)
            {
                foreach (var name in statement.Named)
                {
                    if (named.TryGetValue(name, out var value)) (used ??= new(StringComparer.Ordinal))[name] = value;
                }
            }

            statements[i] = new TerraBaseSqlStatement(statement.Sql, slice, used);
        }

        return statements;
    }
}
