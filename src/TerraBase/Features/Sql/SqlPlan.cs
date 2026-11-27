using System.Collections.Concurrent;
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
    private readonly IReadOnlyList<SqlStatement>? _sql;
    private readonly bool? _writes;

    internal SqlBatch(IReadOnlyList<PlannedStatement> statements, ConsistencyMode? mode, IReadOnlyList<SqlStatement>? sql, bool? writes = null)
        : this(statements, mode)
    {
        _sql = sql;
        _writes = writes;
    }

    public bool Writes => _writes ?? HasWrites(Statements);

    public IReadOnlyList<SqlStatement> Sql => _sql ?? ExtractSql(Statements);

    private static bool HasWrites(IReadOnlyList<PlannedStatement> statements)
    {
        for (var index = 0; index < statements.Count; index++)
        {
            if (statements[index].Kind is StatementKind.Write or StatementKind.Schema)
            {
                return true;
            }
        }

        return false;
    }

    private static SqlStatement[] ExtractSql(IReadOnlyList<PlannedStatement> statements)
    {
        if (statements.Count == 0)
        {
            return [];
        }

        var result = new SqlStatement[statements.Count];
        for (var index = 0; index < statements.Count; index++)
        {
            result[index] = statements[index].Statement;
        }

        return result;
    }
}

internal static class SqlPlan
{
    public const int MaxStatements = 256;

    public const int MaxStatementBytes = SqlLimits.MaxStatementBytes;

    private const int MaxCacheEntries = 4096;

    private const int MaxCachedSqlLength = 4096;

    private static readonly FrozenSet<string> ReadVerbs = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "SELECT", "VALUES", "EXPLAIN", "PRAGMA");

    private static readonly FrozenSet<string> WriteWords = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "INSERT", "UPDATE", "DELETE", "REPLACE", "UPSERT");

    private static readonly FrozenSet<string> SchemaVerbs = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "CREATE", "ALTER", "DROP", "REINDEX");

    private static readonly ConcurrentDictionary<string, ClassifiedSql> Cache = new(StringComparer.Ordinal);

    private static int _cacheCount;

    public static bool TryClassifyCached(string sql, out StatementKind kind)
    {
        if (sql is { Length: > 0 and <= MaxCachedSqlLength } && Cache.TryGetValue(sql, out var cached))
        {
            kind = cached.Kind;
            return true;
        }

        kind = default;
        return false;
    }

    public static StatementPlan Classify(SqlStatement statement, int index)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var sql = statement.Sql;
        if (sql is { Length: > 0 and <= MaxCachedSqlLength } && Cache.TryGetValue(sql, out var cached))
        {
            return new PlannedStatement(statement, cached.Kind, cached.Tokens, cached.BeginMode);
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            return SqlRefusal.Invalid("sql must be a non-empty statement", index);
        }

        if (!SqlLimits.StatementFits(sql))
        {
            return SqlRefusal.From(SqlLimits.StatementTooLong(index));
        }

        if (!SqlText.TryTokenize(sql, out var tokens) || tokens.Count == 0)
        {
            return new SqlRefusal("syntax_error", "the statement has an unterminated quote or no tokens", "42601", index, StatusCodes.Status400BadRequest);
        }

        var count = tokens[^1].Kind == SqlTokenKind.Semicolon ? tokens.Count - 1 : tokens.Count;
        if (count == 0)
        {
            return SqlRefusal.Invalid("sql must be a non-empty statement", index);
        }

        var meaningful = new SqlToken[count];
        tokens.CopyTo(0, meaningful, 0, count);
        var classified = ClassifyTokens(meaningful);
        if (sql.Length <= MaxCachedSqlLength)
        {
            Store(sql, classified);
        }

        return new PlannedStatement(statement, classified.Kind, classified.Tokens, classified.BeginMode);
    }

    public static SqlBatchPlan Batch(IReadOnlyList<SqlStatement> statements, bool allowTransactionControl)
    {
        ArgumentNullException.ThrowIfNull(statements);
        if (statements.Count is 0 or > MaxStatements)
        {
            return SqlRefusal.Invalid("statements holds 1 to 256 statements", 0);
        }

        if (statements.Count == 1)
        {
            return Classify(statements[0], 0) switch
            {
                PlannedStatement single => !allowTransactionControl && single.Kind is StatementKind.Begin or StatementKind.Commit or StatementKind.Rollback
                    ? TransactionControlRefusal(0)
                    : new SqlBatch([single], null, statements, single.Kind is StatementKind.Write or StatementKind.Schema),
                SqlRefusal refusal => refusal,
            };
        }

        var count = statements.Count;
        var planned = new PlannedStatement[count];
        var hasWrites = false;
        for (var index = 0; index < count; index++)
        {
            var failure = Classify(statements[index], index) switch
            {
                PlannedStatement statement => Assign(planned, index, statement),
                SqlRefusal refusal => refusal,
            };
            if (failure is not null)
            {
                return failure;
            }

            if (planned[index].Kind is StatementKind.Write or StatementKind.Schema)
            {
                hasWrites = true;
            }
        }

        ConsistencyMode? mode = null;
        IReadOnlyList<SqlStatement>? sourceSql = statements;
        if (planned[0].Kind == StatementKind.Begin && planned[^1].Kind == StatementKind.Commit)
        {
            mode = planned[0].BeginMode ?? ConsistencyMode.Fast;
            if (planned.Length == 2)
            {
                return SqlRefusal.Invalid("the batch holds no statement between BEGIN and COMMIT", 0);
            }

            planned = planned[1..^1];
            sourceSql = null;
        }

        if (!allowTransactionControl)
        {
            for (var index = 0; index < planned.Length; index++)
            {
                if (planned[index].Kind is StatementKind.Begin or StatementKind.Commit or StatementKind.Rollback)
                {
                    return TransactionControlRefusal(index);
                }
            }
        }

        return new SqlBatch(planned, mode, sourceSql, hasWrites);
    }

    private static ClassifiedSql ClassifyTokens(SqlToken[] meaningful)
    {
        var verb = meaningful[0];
        if (verb.Is("BEGIN") || verb.Is("START"))
        {
            return new ClassifiedSql(StatementKind.Begin, meaningful, BeginMode(meaningful));
        }

        if (verb.Is("COMMIT") || verb.Is("END"))
        {
            return new ClassifiedSql(StatementKind.Commit, meaningful, null);
        }

        if (verb.Is("ROLLBACK") && !ContainsWord(meaningful, "TO"))
        {
            return new ClassifiedSql(StatementKind.Rollback, meaningful, null);
        }

        if (verb.Kind == SqlTokenKind.Word && SchemaVerbs.Contains(verb.Value))
        {
            return new ClassifiedSql(StatementKind.Schema, meaningful, null);
        }

        var reads = verb.Kind == SqlTokenKind.Word && (ReadVerbs.Contains(verb.Value) || verb.Is("WITH"));
        if (!reads)
        {
            return new ClassifiedSql(StatementKind.Write, meaningful, null);
        }

        for (var index = 0; index < meaningful.Length; index++)
        {
            var token = meaningful[index];
            if (token.Kind == SqlTokenKind.Word && WriteWords.Contains(token.Value))
            {
                return new ClassifiedSql(StatementKind.Write, meaningful, null);
            }
        }

        return new ClassifiedSql(StatementKind.Read, meaningful, null);
    }

    private static void Store(string sql, ClassifiedSql classified)
    {
        if (Volatile.Read(ref _cacheCount) >= MaxCacheEntries)
        {
            Cache.Clear();
            Volatile.Write(ref _cacheCount, 0);
        }

        if (Cache.TryAdd(sql, classified))
        {
            Interlocked.Increment(ref _cacheCount);
        }
    }

    private static SqlRefusal TransactionControlRefusal(int index) =>
        new("transaction_state", "BEGIN, COMMIT and ROLLBACK may only wrap the whole batch; every batch already runs as one transaction",
            "25000", index, StatusCodes.Status400BadRequest);

    private static SqlRefusal? Assign(PlannedStatement[] planned, int index, PlannedStatement statement)
    {
        planned[index] = statement;
        return null;
    }

    private static bool ContainsWord(SqlToken[] tokens, string word)
    {
        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].Is(word))
            {
                return true;
            }
        }

        return false;
    }

    private static ConsistencyMode? BeginMode(SqlToken[] tokens)
    {
        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].Is("STRONG"))
            {
                return ConsistencyMode.Strong;
            }
        }

        for (var index = 0; index + 2 < tokens.Length; index++)
        {
            if (tokens[index].Is("ISOLATION") && tokens[index + 1].Is("LEVEL") && tokens[index + 2].Is("SERIALIZABLE"))
            {
                return ConsistencyMode.Strong;
            }
        }

        return null;
    }

    private sealed record ClassifiedSql(StatementKind Kind, SqlToken[] Tokens, ConsistencyMode? BeginMode);
}
