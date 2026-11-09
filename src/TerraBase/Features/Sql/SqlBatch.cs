using System.Diagnostics;
using TerraBase.Errors;

namespace TerraBase.Features.Sql;

internal sealed record SqlBatchFailure(SqlError Error, int Statement);

internal readonly union SqlBatchOutcome(SqlExecuteBody, SqlBatchFailure);

internal readonly union SqlCommandsOutcome(List<SqlPlannedCommand>, SqlBatchFailure);

internal static class SqlBatch
{
    public const int MaxStatements = 256;
    public const int MaxBytes = 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, SqlValue> NoNamed = new Dictionary<string, SqlValue>();

    public static SqlError TooManyStatements { get; } = new(SqlErrors.InvalidRequest, "statements holds 1 to 256 statements");

    public static SqlPlannedCommand Control(string sql) =>
        SqlScript.Classify(sql) is SqlStatementPlan plan ? new(plan, new SqlCommand(sql, [], NoNamed)) : throw new UnreachableException();

    public static SqlCommandsOutcome Commands(IReadOnlyList<SqlExecuteStatement>? statements)
    {
        if (statements is not { Count: > 0 and <= MaxStatements })
        {
            return new SqlBatchFailure(TooManyStatements, 0);
        }

        var commands = new List<SqlPlannedCommand>(statements.Count);
        for (var i = 0; i < statements.Count; i++)
        {
            switch (SqlJson.Command(statements[i]))
            {
                case Validation error:
                    return new SqlBatchFailure(new SqlError(SqlErrors.InvalidRequest, error.Message), i);
                case SqlCommand command:
                    switch (SqlScript.Classify(command.Sql))
                    {
                        case SqlError error:
                            return new SqlBatchFailure(error, i);
                        case SqlStatementPlan { IsControl: true }:
                            return new SqlBatchFailure(new SqlError(SqlErrors.TransactionState,
                                "BEGIN, COMMIT and ROLLBACK are not statements here: a batch runs as one transaction"), i);
                        case SqlStatementPlan plan:
                            commands.Add(new(plan, command));
                            break;
                    }

                    break;
            }
        }

        return commands;
    }

    public static async ValueTask<SqlBatchOutcome> RunAsync(ISqlSession session, IReadOnlyList<SqlPlannedCommand> commands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(commands);
        if (await session.TryBatchAsync(commands, cancellationToken).ConfigureAwait(false) is { } forwarded)
        {
            return forwarded;
        }

        var charges = new List<SqlReadCharge>(commands.Count);
        if (!session.IsAutocommit)
        {
            var inside = await RunInAsync(session, commands, charges, cancellationToken).ConfigureAwait(false);
            charges.ForEach(charge => charge.Accept());
            return inside;
        }

        var write = commands.Any(command => command.Plan.Kind is SqlStatementKind.Write);
        if (await session.ExecuteAsync(Control(write ? "BEGIN" : "BEGIN READ ONLY"), cancellationToken).ConfigureAwait(false) is SqlError refused)
        {
            return new SqlBatchFailure(refused, 0);
        }

        var outcome = await RunInAsync(session, commands, charges, cancellationToken).ConfigureAwait(false);
        if (outcome is SqlBatchFailure failure)
        {
            await RollBackAsync(session, cancellationToken).ConfigureAwait(false);
            return failure;
        }

        if (await session.ExecuteAsync(Control("COMMIT"), cancellationToken).ConfigureAwait(false) is SqlError failed)
        {
            await RollBackAsync(session, cancellationToken).ConfigureAwait(false);
            return new SqlBatchFailure(failed, commands.Count - 1);
        }

        charges.ForEach(charge => charge.Accept());
        return outcome;
    }

    private static async ValueTask RollBackAsync(ISqlSession session, CancellationToken cancellationToken)
    {
        if (!session.IsAutocommit)
        {
            _ = await session.ExecuteAsync(Control("ROLLBACK"), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<SqlBatchOutcome> RunInAsync(ISqlSession session, IReadOnlyList<SqlPlannedCommand> commands,
        List<SqlReadCharge> charges, CancellationToken cancellationToken)
    {
        var results = new List<SqlStatementBody>(commands.Count);
        for (var i = 0; i < commands.Count; i++)
        {
            switch (await session.ExecuteAsync(commands[i].Plan, commands[i].Command, cancellationToken).ConfigureAwait(false))
            {
                case SqlError error:
                    return new SqlBatchFailure(error, i);
                case SqlResult result:
                    if (result.ReadCharge is { } charge)
                    {
                        charges.Add(charge);
                    }

                    results.Add(SqlStatementBody.Of(result));
                    break;
            }
        }

        return new SqlExecuteBody(results);
    }

    public static SqlErrorBody Body(SqlBatchFailure failure, string? requestId = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new SqlErrorBody(new SqlErrorDetail(failure.Error.Code, failure.Error.Message, failure.Statement, failure.Error.Sqlstate, requestId, ErrorDocs.Url(failure.Error.Code)));
    }
}
