using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using TerraBase.Features.Sql;

namespace TerraBase.Features.Rules;

internal sealed record EnforcedSql(IReadOnlyList<SqlStatement> Statements, string? RulesVersion);

internal sealed class RulesEnforcer(RulesStore store, TimeProvider time)
{
    private static readonly FrozenSet<string> ClauseEnds = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "WHERE", "GROUP", "ORDER", "LIMIT", "HAVING", "WINDOW", "UNION", "EXCEPT", "INTERSECT", "ON", "USING", "RETURNING", "SET", "VALUES",
        "JOIN", "LEFT", "RIGHT", "FULL", "INNER", "CROSS", "NATURAL", "OUTER", "OFFSET");

    private static readonly FrozenSet<string> TailClauses = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "RETURNING", "ORDER", "LIMIT");

    private static readonly FrozenSet<string> SqlKeywords = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "TRUE", "FALSE", "AS", "BY", "ORDER", "ASC", "DESC",
        "LIMIT", "OFFSET", "GROUP", "HAVING", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "ON", "USING", "CASE", "WHEN",
        "THEN", "ELSE", "END", "CAST", "COLLATE", "LIKE", "GLOB", "BETWEEN", "DISTINCT", "ALL", "UNION", "INTERSECT", "EXCEPT",
        "EXISTS", "WITH", "RECURSIVE", "UPDATE", "SET", "DELETE", "INSERT", "INTO", "VALUES", "RETURNING", "ROWID", "_ROWID_", "OID");

    public async ValueTask<Result<EnforcedSql, SqlRefusal>> ApplyAsync(
        Ulid databaseId, RulesAuth auth, IReadOnlyList<PlannedStatement> statements, CancellationToken cancellationToken, ISqlDatabaseSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(statements);
        var loaded = await store.CurrentAsync(databaseId, cancellationToken);
        return loaded switch
        {
            StoredRules current => await RewriteAllAsync(current.Rules, current.Version, auth, statements, session, cancellationToken),
            NotFound => await DenyMissingRulesAsync(auth, statements, session, cancellationToken),
            Unavailable unavailable => SqlRefusal.From(unavailable),
        };
    }

    private async ValueTask<Result<EnforcedSql, SqlRefusal>> DenyMissingRulesAsync(
        RulesAuth auth, IReadOnlyList<PlannedStatement> statements, ISqlDatabaseSession? session, CancellationToken cancellationToken)
    {
        var checkedEmpty = await RewriteAllAsync(new RulesSet(new Dictionary<string, RulesTable>()), "", auth, statements, session, cancellationToken);
        return checkedEmpty switch
        {
            SqlRefusal refusal => refusal,
            EnforcedSql => SqlRefusal.RuleDenied("the database has no rules, so end users are denied", 0),
        };
    }

    private async ValueTask<Result<EnforcedSql, SqlRefusal>> RewriteAllAsync(
        RulesSet rules, string rulesVersion, RulesAuth auth, IReadOnlyList<PlannedStatement> statements, ISqlDatabaseSession? session, CancellationToken cancellationToken)
    {
        List<SqlStatement> rewritten = [];
        for (var index = 0; index < statements.Count; index++)
        {
            var outcome = await RewriteAsync(rules, auth, statements[index], index, session, cancellationToken);
            var failure = outcome switch
            {
                SqlStatement statement => Add(rewritten, statement),
                SqlRefusal refusal => refusal,
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        return new EnforcedSql(rewritten, rulesVersion);
    }

    public async ValueTask<Result<SqlStatement, SqlRefusal>> RewriteAsync(
        RulesSet rules, RulesAuth auth, PlannedStatement planned, int index, ISqlDatabaseSession? session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(planned);
        var tokens = planned.Tokens;
        var sql = planned.Statement.Sql;
        return planned.Kind switch
        {
            StatementKind.Schema or StatementKind.Begin or StatementKind.Commit or StatementKind.Rollback =>
                SqlRefusal.RuleDenied("end users cannot change the schema or control transactions", index),
            StatementKind.Read => await ReadsAsync(rules, auth, sql, tokens, index, skipTarget: -1, session, cancellationToken) switch
            {
                string text => planned.Statement with { Sql = text },
                SqlRefusal refusal => refusal,
            },
            StatementKind.Write => await WriteAsync(rules, auth, planned, index, session, cancellationToken),
            _ => throw new UnreachableException(),
        };
    }

    private async ValueTask<Result<SqlStatement, SqlRefusal>> WriteAsync(
        RulesSet rules, RulesAuth auth, PlannedStatement planned, int index, ISqlDatabaseSession? session, CancellationToken cancellationToken)
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
            return SqlRefusal.RuleDenied(string.Concat("the rules do not allow ", deleting ? "deleting from " : "updating ", table), index,
                deleting ? "delete" : "update", table);
        }

        if (!deleting && rule.Writable is { } writable && Assigned(tokens, targetAt) is { } columns
            && columns.FirstOrDefault(column => !writable.Contains(column, StringComparer.OrdinalIgnoreCase)) is { } blocked)
        {
            return new SqlRefusal("column_not_writable", string.Concat(blocked, " is not a writable column of ", table), "42501", index, StatusCodes.Status403Forbidden,
                Details: new Dictionary<string, string>(StringComparer.Ordinal) { ["column"] = blocked });
        }

        var compiled = RulesCompiler.Compile(condition, null, operation) switch
        {
            CompiledCondition ok when !ok.UsesNew => ok,
            CompiledCondition or Validation => null,
        };
        if (compiled is null)
        {
            return SqlRefusal.RuleDenied(string.Concat("the ", deleting ? "delete" : "update", " rule of ", table, " cannot run on SQL statements; use the rows API"), index,
                deleting ? "delete" : "update", table);
        }

        var predicate = RulesCompiler.Render(compiled, auth, time.GetUtcNow().ToUnixTimeMilliseconds());
        var rewrittenReads = await ReadsAsync(rules, auth, planned.Statement.Sql, tokens, index, skipTarget: targetAt, session, cancellationToken);
        return rewrittenReads switch
        {
            string text => SqlText.TryTokenize(text, out var rewritten)
                ? planned.Statement with { Sql = AppendWhere(text, rewritten, predicate) }
                : SqlRefusal.RuleDenied("the rewritten statement could not be read", index),
            SqlRefusal refusal => refusal,
        };
    }

    private async ValueTask<Result<string, SqlRefusal>> ReadsAsync(
        RulesSet rules, RulesAuth auth, string sql, IReadOnlyList<SqlToken> tokens, int index, int skipTarget, ISqlDatabaseSession? session, CancellationToken cancellationToken)
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
                return SqlRefusal.RuleDenied(string.Concat("the rules do not allow reading ", table), index, "read", table);
            }

            IReadOnlyList<string>? liveNames = null;
            if (session is not null)
            {
                var tableColumns = await RulesService.TableColumnsAsync(session, table, cancellationToken);
                if (tableColumns.Count > 0)
                {
                    liveNames = [.. tableColumns.Select(static column => column.Name)];
                    if (rule.Columns is { Count: > 0 } listed && listed.Any(column => !liveNames.Contains(column, StringComparer.OrdinalIgnoreCase)))
                    {
                        return SqlRefusal.RulesInvalid(string.Concat("the read columns of ", table, " reference a missing column; deploy the rules again"), index);
                    }
                }
            }

            var compiled = RulesCompiler.Compile(read, liveNames, RulesOperation.Read) switch
            {
                CompiledCondition condition => condition,
                Validation => null,
            };
            if (compiled is null)
            {
                return SqlRefusal.RulesInvalid(string.Concat("the read rule of ", table, " no longer compiles; deploy the rules again"), index);
            }

            if (liveNames is not null && rule.Columns is { Count: > 0 } visibleColumns
                && FindHiddenColumn(tokens, at, liveNames, visibleColumns) is { } hiddenColumn)
            {
                return SqlRefusal.ColumnNotReadable(hiddenColumn, index);
            }

            var includeRowid = tokens.Any(static token => token.Is("rowid") || token.Is("_rowid_") || token.Is("oid"));
            var columns = rule.Columns is { Count: > 0 } readable
                ? (includeRowid && !readable.Any(static column => string.Equals(column, "rowid", StringComparison.OrdinalIgnoreCase))
                    ? string.Concat("rowid, ", string.Join(", ", readable.Select(RulesCompiler.Quote)))
                    : string.Join(", ", readable.Select(RulesCompiler.Quote)))
                : (includeRowid ? "rowid, *" : "*");
            var aliased = at + 1 < tokens.Count && (tokens[at + 1].Is("AS") || (tokens[at + 1].IsName && !(tokens[at + 1].Kind == SqlTokenKind.Word && ClauseEnds.Contains(tokens[at + 1].Value))));
            output.Append(sql, cursor, tokens[start].Start - cursor);
            output.Append("(SELECT ").Append(columns).Append(" FROM ").Append(RulesCompiler.Quote(table)).Append(" WHERE ")
                .Append(RulesCompiler.Render(compiled, auth, time.GetUtcNow().ToUnixTimeMilliseconds())).Append(')');
            if (!aliased)
            {
                output.Append(" AS ").Append(RulesCompiler.Quote(table));
            }

            cursor = tokens[at].End;
        }

        output.Append(sql, cursor, sql.Length - cursor);
        return output.ToString();
    }

    private static string? FindHiddenColumn(IReadOnlyList<SqlToken> tokens, int tableAt, IReadOnlyList<string> liveNames, IReadOnlyList<string> visibleColumns)
    {
        Dictionary<string, string>? hidden = null;
        foreach (var name in liveNames)
        {
            if (!visibleColumns.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                hidden ??= new(StringComparer.OrdinalIgnoreCase);
                hidden.TryAdd(name, name);
            }
        }

        if (hidden is null)
        {
            return null;
        }

        for (var at = 0; at < tokens.Count; at++)
        {
            var token = tokens[at];
            if (!token.IsName || at == tableAt)
            {
                continue;
            }

            if (at > 0 && tokens[at - 1].Is("AS"))
            {
                continue;
            }

            if (at + 1 < tokens.Count && tokens[at + 1].Kind == SqlTokenKind.Open)
            {
                continue;
            }

            if (token.Kind == SqlTokenKind.Word && SqlKeywords.Contains(token.Value))
            {
                continue;
            }

            if (hidden.TryGetValue(token.Value, out var matched))
            {
                return matched;
            }
        }

        return null;
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
