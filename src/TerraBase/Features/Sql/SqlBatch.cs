using TerraBase.Errors;

namespace TerraBase.Features.Sql;

internal sealed record SqlBatchFailure(SqlError Error, int Statement);

internal readonly union SqlBatchOutcome(SqlExecuteBody, SqlBatchFailure);

internal readonly union SqlCommandsOutcome(List<SqlCommand>, SqlBatchFailure);

internal static class SqlBatch
{
    public const int MaxStatements = 256;
    public const int MaxBytes = 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, SqlValue> NoNamed = new Dictionary<string, SqlValue>();

    public static SqlError TooManyStatements { get; } = new(SqlErrors.InvalidRequest, "statements holds 1 to 256 statements");

    public static SqlCommand Control(string sql) => new(sql, [], NoNamed);

    public static SqlCommandsOutcome Commands(IReadOnlyList<SqlExecuteStatement>? statements)
    {
        if (statements is not { Count: > 0 and <= MaxStatements })
        {
            return new SqlBatchFailure(TooManyStatements, 0);
        }

        var commands = new List<SqlCommand>(statements.Count);
        for (var i = 0; i < statements.Count; i++)
        {
            switch (SqlJson.Command(statements[i]))
            {
                case Validation error:
                    return new SqlBatchFailure(new SqlError(SqlErrors.InvalidRequest, error.Message), i);
                case SqlCommand command:
                    if (SqlScript.Classify(command.Sql) is SqlStatementPlan { IsControl: true })
                    {
                        return new SqlBatchFailure(new SqlError(SqlErrors.TransactionState,
                            "BEGIN, COMMIT and ROLLBACK are not statements here: a batch runs as one transaction"), i);
                    }

                    commands.Add(command);
                    break;
            }
        }

        return commands;
    }

    public static async ValueTask<SqlBatchOutcome> RunAsync(ISqlSession session, IReadOnlyList<SqlCommand> commands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(commands);
        var charges = new List<SqlReadCharge>(commands.Count);
        if (!session.IsAutocommit)
        {
            var inside = await RunInAsync(session, commands, charges, cancellationToken).ConfigureAwait(false);
            charges.ForEach(charge => charge.Accept());
            return inside;
        }

        var write = commands.Any(command => SqlScript.Classify(command.Sql) is SqlStatementPlan { Kind: SqlStatementKind.Write });
        if (await session.ExecuteAsync(Control(write ? "BEGIN" : "BEGIN READ ONLY"), cancellationToken).ConfigureAwait(false) is SqlError refused)
        {
            return new SqlBatchFailure(refused, 0);
        }

        var outcome = await RunInAsync(session, commands, charges, cancellationToken).ConfigureAwait(false);
        if (outcome is SqlBatchFailure)
        {
            if (!session.IsAutocommit)
            {
                _ = await session.ExecuteAsync(Control("ROLLBACK"), cancellationToken).ConfigureAwait(false);
            }

            return outcome;
        }

        if (await session.ExecuteAsync(Control("COMMIT"), cancellationToken).ConfigureAwait(false) is SqlError failed)
        {
            if (!session.IsAutocommit)
            {
                _ = await session.ExecuteAsync(Control("ROLLBACK"), cancellationToken).ConfigureAwait(false);
            }

            return new SqlBatchFailure(failed, commands.Count - 1);
        }

        charges.ForEach(charge => charge.Accept());
        return outcome;
    }

    private static async ValueTask<SqlBatchOutcome> RunInAsync(ISqlSession session, IReadOnlyList<SqlCommand> commands, List<SqlReadCharge> charges,
        CancellationToken cancellationToken)
    {
        var results = new List<SqlStatementBody>(commands.Count);
        for (var i = 0; i < commands.Count; i++)
        {
            switch (await session.ExecuteAsync(commands[i], cancellationToken).ConfigureAwait(false))
            {
                case SqlError error:
                    return new SqlBatchFailure(error, i);
                case SqlResult result:
                    if (result.ReadCharge is { } charge)
                    {
                        charges.Add(charge);
                    }

                    results.Add(new SqlStatementBody(
                        [.. result.Columns.Select(column => new SqlColumnBody(column.Name, column.DeclType))], result.Rows, result.RowsAffected, result.LastInsertRowid));
                    break;
            }
        }

        return new SqlExecuteBody(results);
    }

    public static SqlErrorBody Body(SqlBatchFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new SqlErrorBody(new SqlErrorDetail(failure.Error.Code, failure.Error.Message, failure.Statement, failure.Error.Sqlstate));
    }
}
