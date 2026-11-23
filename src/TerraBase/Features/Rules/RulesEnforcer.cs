using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using TerraBase.Features.Sql;

namespace TerraBase.Features.Rules;

internal sealed class RulesEnforcer(RulesStore store, TimeProvider time)
{
    private static readonly FrozenSet<string> ClauseEnds = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "WHERE", "GROUP", "ORDER", "LIMIT", "HAVING", "WINDOW", "UNION", "EXCEPT", "INTERSECT", "ON", "USING", "RETURNING", "SET", "VALUES",
        "JOIN", "LEFT", "RIGHT", "FULL", "INNER", "CROSS", "NATURAL", "OUTER", "OFFSET");

    private static readonly FrozenSet<string> TailClauses = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "RETURNING", "ORDER", "LIMIT");

    public async ValueTask<Result<IReadOnlyList<SqlStatement>, SqlRefusal>> ApplyAsync(
        Ulid databaseId, RulesAuth auth, IReadOnlyList<PlannedStatement> statements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(statements);
        return await store.CurrentAsync(databaseId, cancellationToken) switch
        {
            StoredRules current => RewriteAll(current.Rules, auth, statements),
            NotFound => SqlRefusal.RuleDenied("the database has no rules, so end users are denied", 0),
            Unavailable unavailable => SqlRefusal.From(unavailable),
        };
    }

    private Result<IReadOnlyList<SqlStatement>, SqlRefusal> RewriteAll(RulesSet rules, RulesAuth auth, IReadOnlyList<PlannedStatement> statements)
    {
        List<SqlStatement> rewritten = [];
        for (var index = 0; index < statements.Count; index++)
        {
            var failure = Rewrite(rules, auth, statements[index], index) switch
            {
                SqlStatement statement => Add(rewritten, statement),
                SqlRefusal refusal => refusal,
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        return Rewritten(rewritten);
    }

    private static Result<IReadOnlyList<SqlStatement>, SqlRefusal> Rewritten(List<SqlStatement> statements) => statements;

    public Result<SqlStatement, SqlRefusal> Rewrite(RulesSet rules, RulesAuth auth, PlannedStatement planned, int index)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(planned);
        var tokens = planned.Tokens;
        var sql = planned.Statement.Sql;
        return planned.Kind switch
        {
            StatementKind.Schema or StatementKind.Begin or StatementKind.Commit or StatementKind.Rollback =>
                SqlRefusal.RuleDenied("end users cannot change the schema or control transactions", index),
            StatementKind.Read => Reads(rules, auth, sql, tokens, index, skipTarget: -1) switch
            {
                string text => planned.Statement with { Sql = text },
                SqlRefusal refusal => refusal,
            },
            StatementKind.Write => Write(rules, auth, planned, index),
            _ => throw new UnreachableException(),
        };
    }

    private Result<SqlStatement, SqlRefusal> Write(RulesSet rules, RulesAuth auth, PlannedStatement planned, int index)
    {
        var tokens = planned.Tokens;
        var verbAt = FirstTopLevel(tokens, static token => token.Is("UPDATE") || token.Is("DELETE") || token.Is("INSERT") || token.Is("REPLACE"));
        if (verbAt < 0 || tokens[verbAt].Is("INSERT") || tokens[verbAt].Is("REPLACE"))
        {
            return SqlRefusal.RuleDenied("end users insert rows through the rows API, which checks the create rule", index);
        }

        var deleting = tokens[verbAt].Is("DELETE");
        var targetAt = verbAt + 1;
        if (deleting && targetAt < tokens.Count && tokens[targetAt].Is("FROM"))
        {
            targetAt++;
        }

        if (targetAt < tokens.Count && tokens[targetAt].Is("OR"))
        {
            targetAt += 2;
        }

        if (targetAt >= tokens.Count || !tokens[targetAt].IsName)
        {
            return SqlRefusal.RuleDenied("the statement's target table could not be read", index);
        }

        if (targetAt + 2 < tokens.Count && tokens[targetAt + 1].Kind == SqlTokenKind.Dot && tokens[targetAt + 2].IsName)
        {
            targetAt += 2;
        }

        var table = tokens[targetAt].Value;
        var operation = deleting ? RulesOperation.Delete : RulesOperation.Update;
        var rule = rules.Table(table);
        var condition = deleting ? rule?.Delete : rule?.Update;
        if (rule is null || condition is null)
        {
            return SqlRefusal.RuleDenied(string.Concat("the rules do not allow ", deleting ? "deleting from " : "updating ", table), index);
        }

        if (!deleting && rule.Writable is { } writable && Assigned(tokens, targetAt) is { } columns
            && columns.FirstOrDefault(column => !writable.Contains(column, StringComparer.OrdinalIgnoreCase)) is { } blocked)
        {
            return new SqlRefusal("column_not_writable", string.Concat(blocked, " is not a writable column of ", table), "42501", index, StatusCodes.Status403Forbidden);
        }

        var compiled = RulesCompiler.Compile(condition, null, operation) switch
        {
            CompiledCondition ok when !ok.UsesNew => ok,
            CompiledCondition or Validation => null,
        };
        if (compiled is null)
        {
            return SqlRefusal.RuleDenied(string.Concat("the ", deleting ? "delete" : "update", " rule of ", table, " cannot run on SQL statements; use the rows API"), index);
        }

        var predicate = RulesCompiler.Render(compiled, auth, time.GetUtcNow());
        return Reads(rules, auth, planned.Statement.Sql, tokens, index, skipTarget: targetAt) switch
        {
            string text => SqlText.TryTokenize(text, out var rewritten)
                ? planned.Statement with { Sql = AppendWhere(text, rewritten, predicate) }
                : SqlRefusal.RuleDenied("the rewritten statement could not be read", index),
            SqlRefusal refusal => refusal,
        };
    }

    private Result<string, SqlRefusal> Reads(RulesSet rules, RulesAuth auth, string sql, IReadOnlyList<SqlToken> tokens, int index, int skipTarget)
    {
        var ctes = CteNames(tokens);
        var output = new StringBuilder(sql.Length + 128);
        var cursor = 0;
        var depth = 0;
        Stack<int> fromDepths = [];
        var expectTable = false;
        for (var at = 0; at < tokens.Count; at++)
        {
            var token = tokens[at];
            switch (token.Kind)
            {
                case SqlTokenKind.Open:
                    depth++;
                    expectTable = false;
                    continue;
                case SqlTokenKind.Close:
                    depth--;
                    while (fromDepths.Count > 0 && fromDepths.Peek() > depth)
                    {
                        fromDepths.Pop();
                    }

                    continue;
            }

            if (token.Is("FROM") || token.Is("JOIN"))
            {
                if (token.Is("FROM"))
                {
                    fromDepths.Push(depth);
                }

                expectTable = true;
                continue;
            }

            if (token.Kind == SqlTokenKind.Comma && fromDepths.Count > 0 && fromDepths.Peek() == depth)
            {
                expectTable = true;
                continue;
            }

            if (token.Kind == SqlTokenKind.Word && ClauseEnds.Contains(token.Value) && fromDepths.Count > 0 && fromDepths.Peek() == depth && !token.Is("JOIN"))
            {
                if (!(token.Is("LEFT") || token.Is("RIGHT") || token.Is("FULL") || token.Is("INNER") || token.Is("CROSS") || token.Is("NATURAL") || token.Is("OUTER")))
                {
                    fromDepths.Pop();
                }

                expectTable = false;
                continue;
            }

            if (!expectTable || !token.IsName)
            {
                expectTable = false;
                continue;
            }

            expectTable = false;
            var start = at;
            if (at + 2 < tokens.Count && tokens[at + 1].Kind == SqlTokenKind.Dot && tokens[at + 2].IsName && token.Is("main"))
            {
                at += 2;
            }

            if (at + 1 < tokens.Count && tokens[at + 1].Kind == SqlTokenKind.Open)
            {
                continue;
            }

            var table = tokens[at].Value;
            if (at == skipTarget || ctes.Contains(table))
            {
                continue;
            }

            if (rules.Table(table) is not { Read: { } read } rule)
            {
                return SqlRefusal.RuleDenied(string.Concat("the rules do not allow reading ", table), index);
            }

            var compiled = RulesCompiler.Compile(read, null, RulesOperation.Read) switch
            {
                CompiledCondition condition => condition,
                Validation => null,
            };
            if (compiled is null)
            {
                return new SqlRefusal("rules_invalid", string.Concat("the read rule of ", table, " no longer compiles; deploy the rules again"), "42501", index,
                    StatusCodes.Status403Forbidden);
            }

            var columns = rule.Columns is { Count: > 0 } readable ? string.Join(", ", readable.Select(RulesCompiler.Quote)) : "*";
            var aliased = at + 1 < tokens.Count && (tokens[at + 1].Is("AS") || (tokens[at + 1].IsName && !(tokens[at + 1].Kind == SqlTokenKind.Word && ClauseEnds.Contains(tokens[at + 1].Value))));
            output.Append(sql, cursor, tokens[start].Start - cursor);
            output.Append("(SELECT ").Append(columns).Append(" FROM ").Append(RulesCompiler.Quote(table)).Append(" WHERE ")
                .Append(RulesCompiler.Render(compiled, auth, time.GetUtcNow())).Append(')');
            if (!aliased)
            {
                output.Append(" AS ").Append(RulesCompiler.Quote(table));
            }

            cursor = tokens[at].End;
        }

        output.Append(sql, cursor, sql.Length - cursor);
        return output.ToString();
    }

    private static string AppendWhere(string sql, List<SqlToken> tokens, string predicate)
    {
        var depth = 0;
        var whereAt = -1;
        var tailAt = -1;
        for (var at = 0; at < tokens.Count; at++)
        {
            var token = tokens[at];
            depth += token.Kind switch
            {
                SqlTokenKind.Open => 1,
                SqlTokenKind.Close => -1,
                SqlTokenKind.Word or SqlTokenKind.Quoted or SqlTokenKind.String or SqlTokenKind.Blob or SqlTokenKind.Number or SqlTokenKind.Parameter
                    or SqlTokenKind.Semicolon or SqlTokenKind.Dot or SqlTokenKind.Comma or SqlTokenKind.Symbol => 0,
                _ => throw new UnreachableException(),
            };
            if (depth != 0 || token.Kind != SqlTokenKind.Word)
            {
                continue;
            }

            if (token.Is("WHERE") && whereAt < 0)
            {
                whereAt = at;
            }
            else if (TailClauses.Contains(token.Value) && tailAt < 0)
            {
                tailAt = at;
            }
        }

        var end = tailAt >= 0 ? tokens[tailAt].Start : tokens.Count > 0 && tokens[^1].Kind == SqlTokenKind.Semicolon ? tokens[^1].Start : sql.Length;
        if (whereAt < 0)
        {
            return string.Concat(sql[..end].TrimEnd(), " WHERE ", predicate, " ", sql[end..]);
        }

        var bodyStart = tokens[whereAt].End;
        return string.Concat(sql[..bodyStart], " (", sql[bodyStart..end].Trim(), ") AND ", predicate, " ", sql[end..]);
    }

    private static List<string>? Assigned(IReadOnlyList<SqlToken> tokens, int targetAt)
    {
        var setAt = -1;
        for (var at = targetAt; at < tokens.Count; at++)
        {
            if (tokens[at].Is("SET"))
            {
                setAt = at;
                break;
            }
        }

        if (setAt < 0)
        {
            return null;
        }

        List<string> columns = [];
        var depth = 0;
        for (var at = setAt + 1; at < tokens.Count; at++)
        {
            var token = tokens[at];
            if (token.Kind == SqlTokenKind.Open)
            {
                depth++;
            }
            else if (token.Kind == SqlTokenKind.Close)
            {
                depth--;
            }
            else if (depth == 0 && (token.Is("WHERE") || token.Is("FROM") || token.Is("RETURNING")))
            {
                break;
            }
            else if (depth == 0 && token.IsName && at + 1 < tokens.Count && tokens[at + 1] is { Kind: SqlTokenKind.Symbol, Value: "=" })
            {
                columns.Add(token.Value);
            }
        }

        return columns;
    }

    private static FrozenSet<string> CteNames(IReadOnlyList<SqlToken> tokens)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        for (var at = 0; at + 1 < tokens.Count; at++)
        {
            if (tokens[at].IsName && (tokens[at + 1].Is("AS") || tokens[at + 1].Kind == SqlTokenKind.Open)
                && at > 0 && (tokens[at - 1].Is("WITH") || tokens[at - 1].Is("RECURSIVE") || tokens[at - 1].Kind == SqlTokenKind.Comma))
            {
                names.Add(tokens[at].Value);
            }
        }

        return names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int FirstTopLevel(IReadOnlyList<SqlToken> tokens, Func<SqlToken, bool> match)
    {
        var depth = 0;
        for (var at = 0; at < tokens.Count; at++)
        {
            depth += tokens[at].Kind == SqlTokenKind.Open ? 1 : tokens[at].Kind == SqlTokenKind.Close ? -1 : 0;
            if (depth == 0 && match(tokens[at]))
            {
                return at;
            }
        }

        return -1;
    }

    private static SqlRefusal? Add(List<SqlStatement> statements, SqlStatement statement)
    {
        statements.Add(statement);
        return null;
    }
}
