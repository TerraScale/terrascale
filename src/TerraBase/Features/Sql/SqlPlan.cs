using System.Collections.Frozen;
using TerraBase.Storage;

namespace TerraBase.Features.Sql;

internal readonly union StatementPlan(PlannedStatement, SqlRefusal);

internal readonly union SqlBatchPlan(SqlBatch, SqlRefusal);

internal enum StatementKind
{
    Read,
    Write,
    Schema,
    Begin,
    Commit,
    Rollback,
}

internal sealed record PlannedStatement(SqlStatement Statement, StatementKind Kind, IReadOnlyList<SqlToken> Tokens, ConsistencyMode? BeginMode);

internal sealed record SqlBatch(IReadOnlyList<PlannedStatement> Statements, ConsistencyMode? Mode)
{
    public bool Writes => Statements.Any(static statement => statement.Kind is StatementKind.Write or StatementKind.Schema);

    public IReadOnlyList<SqlStatement> Sql => [.. Statements.Select(static statement => statement.Statement)];
}

internal static class SqlPlan
{
    public const int MaxStatements = 256;

    public const int MaxStatementBytes = SqlLimits.MaxStatementBytes;

    private static readonly FrozenSet<string> ReadVerbs = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "SELECT", "VALUES", "EXPLAIN", "PRAGMA");

    private static readonly FrozenSet<string> WriteWords = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "INSERT", "UPDATE", "DELETE", "REPLACE", "UPSERT");

    private static readonly FrozenSet<string> SchemaVerbs = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "CREATE", "ALTER", "DROP", "REINDEX");

    public static StatementPlan Classify(SqlStatement statement, int index)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (string.IsNullOrWhiteSpace(statement.Sql))
        {
            return SqlRefusal.Invalid("sql must be a non-empty statement", index);
        }

        if (!SqlLimits.StatementFits(statement.Sql))
        {
            return SqlRefusal.From(SqlLimits.StatementTooLong(index));
        }

        if (!SqlText.TryTokenize(statement.Sql, out var tokens) || tokens.Count == 0)
        {
            return new SqlRefusal("syntax_error", "the statement has an unterminated quote or no tokens", "42601", index, StatusCodes.Status400BadRequest);
        }

        var meaningful = tokens[^1].Kind == SqlTokenKind.Semicolon ? tokens[..^1] : tokens;
        if (meaningful is not [var verb, ..])
        {
            return SqlRefusal.Invalid("sql must be a non-empty statement", index);
        }

        if (verb.Is("BEGIN") || verb.Is("START"))
        {
            return new PlannedStatement(statement, StatementKind.Begin, meaningful, BeginMode(meaningful));
        }

        if (verb.Is("COMMIT") || verb.Is("END"))
        {
            return new PlannedStatement(statement, StatementKind.Commit, meaningful, null);
        }

        if (verb.Is("ROLLBACK") && !meaningful.Any(static token => token.Is("TO")))
        {
            return new PlannedStatement(statement, StatementKind.Rollback, meaningful, null);
        }

        if (verb.Kind == SqlTokenKind.Word && SchemaVerbs.Contains(verb.Value))
        {
            return new PlannedStatement(statement, StatementKind.Schema, meaningful, null);
        }

        var writes = meaningful.Any(static token => token.Kind == SqlTokenKind.Word && WriteWords.Contains(token.Value));
        var reads = verb.Kind == SqlTokenKind.Word && (ReadVerbs.Contains(verb.Value) || verb.Is("WITH"));
        return new PlannedStatement(statement, reads && !writes ? StatementKind.Read : StatementKind.Write, meaningful, null);
    }

    public static SqlBatchPlan Batch(IReadOnlyList<SqlStatement> statements, bool allowTransactionControl)
    {
        ArgumentNullException.ThrowIfNull(statements);
        if (statements.Count is 0 or > MaxStatements)
        {
            return SqlRefusal.Invalid("statements holds 1 to 256 statements", 0);
        }

        List<PlannedStatement> planned = [];
        for (var index = 0; index < statements.Count; index++)
        {
            var failure = Classify(statements[index], index) switch
            {
                PlannedStatement statement => Add(planned, statement),
                SqlRefusal refusal => refusal,
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        ConsistencyMode? mode = null;
        if (planned is [{ Kind: StatementKind.Begin } begin, .., { Kind: StatementKind.Commit }] && planned.Count >= 2)
        {
            mode = begin.BeginMode ?? ConsistencyMode.Fast;
            planned = planned[1..^1];
        }

        for (var index = 0; index < planned.Count; index++)
        {
            if (!allowTransactionControl && planned[index].Kind is StatementKind.Begin or StatementKind.Commit or StatementKind.Rollback)
            {
                return new SqlRefusal("transaction_state", "BEGIN, COMMIT and ROLLBACK may only wrap the whole batch; every batch already runs as one transaction",
                    "25000", index, StatusCodes.Status400BadRequest);
            }
        }

        return planned.Count == 0 ? SqlRefusal.Invalid("the batch holds no statement between BEGIN and COMMIT", 0) : new SqlBatch(planned, mode);
    }

    private static SqlRefusal? Add(List<PlannedStatement> planned, PlannedStatement statement)
    {
        planned.Add(statement);
        return null;
    }

    private static ConsistencyMode? BeginMode(List<SqlToken> tokens)
    {
        if (tokens.Any(static token => token.Is("STRONG")))
        {
            return ConsistencyMode.Strong;
        }

        for (var index = 0; index + 2 < tokens.Count; index++)
        {
            if (tokens[index].Is("ISOLATION") && tokens[index + 1].Is("LEVEL") && tokens[index + 2].Is("SERIALIZABLE"))
            {
                return ConsistencyMode.Strong;
            }
        }

        return null;
    }
}
