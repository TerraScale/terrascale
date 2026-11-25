using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using TerraBase.Engine;
using TerraBase.Features.Sql;
using TerraBase.Hosting;

namespace TerraBase.Features.Rules;

internal readonly union EnforcementOutcome(EnforcedSql, SqlRefusal);

internal readonly union StatementEnforcement(SqlStatement, SqlRefusal);

internal readonly union ReadConditionOutcome(RewrittenSql, SqlRefusal);

internal readonly union EndUserStatementOutcome(EndUserStatement, SqlRefusal);



internal sealed record EnforcedSql(IReadOnlyList<SqlStatement> Statements, string? RulesVersion);

internal sealed record RewrittenSql(
    IReadOnlyList<string> Pieces, IReadOnlyDictionary<string, string> Guards, IReadOnlyDictionary<string, SqlTableScope> Tables);

internal sealed record LiveTable(IReadOnlyList<string> Names, IReadOnlyList<string> Declared, string? RowIdAlias, string? RowIdSource);

internal sealed record RuleSource(int Group, string? Table, string Alias);

internal sealed record EndUserStatement(string Sql, IReadOnlyList<SqlToken> Tokens, int Start, int Main, FrozenSet<string> Ctes);

internal sealed class RulesEnforcer(RulesStore store, TimeProvider time)
{
    private static readonly FrozenSet<string> ClauseEnds = FrozenSet.Create(AsciiIgnoreCaseComparer.Instance,
        "WHERE", "GROUP", "ORDER", "LIMIT", "HAVING", "WINDOW", "UNION", "EXCEPT", "INTERSECT", "ON", "USING", "RETURNING", "SET", "VALUES",
        "JOIN", "LEFT", "RIGHT", "FULL", "INNER", "CROSS", "NATURAL", "OUTER", "OFFSET");

    private static readonly FrozenSet<string> JoinWords = FrozenSet.Create(AsciiIgnoreCaseComparer.Instance,
        "LEFT", "RIGHT", "FULL", "INNER", "CROSS", "NATURAL", "OUTER", "ON", "USING");

    private const string GuardPrefix = "_tb_rule_";

    private static readonly FrozenSet<string> TailClauses = FrozenSet.Create(AsciiIgnoreCaseComparer.Instance, "RETURNING", "ORDER", "LIMIT");

    private static readonly FrozenSet<string> RowIdNames = FrozenSet.Create(AsciiIgnoreCaseComparer.Instance, "rowid", "_rowid_", "oid");

    private static readonly FrozenSet<string> TableFunctions = FrozenSet.Create(AsciiIgnoreCaseComparer.Instance, "json_each", "json_tree");

    private static readonly FrozenSet<string> SqlKeywords = FrozenSet.Create(AsciiIgnoreCaseComparer.Instance,
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "TRUE", "FALSE", "AS", "BY", "ORDER", "ASC", "DESC",
        "LIMIT", "OFFSET", "GROUP", "HAVING", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "ON", "USING", "CASE", "WHEN",
        "THEN", "ELSE", "END", "CAST", "COLLATE", "LIKE", "GLOB", "BETWEEN", "DISTINCT", "ALL", "UNION", "INTERSECT", "EXCEPT",
        "EXISTS", "WITH", "RECURSIVE", "UPDATE", "SET", "DELETE", "INSERT", "INTO", "VALUES", "RETURNING", "ROWID", "_ROWID_", "OID");

    public async ValueTask<EnforcementOutcome> ApplyAsync(
        Ulid databaseId, RulesAuth auth, IReadOnlyList<PlannedStatement> statements, CancellationToken cancellationToken, ISqlDatabaseSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(statements);
        var loaded = await store.CurrentAsync(databaseId, cancellationToken);
        return loaded switch
        {
            StoredRules current => await RewriteAllAsync(current.Rules, current.Version, auth, statements, session, cancellationToken),
            ResourceNotFound => await DenyMissingRulesAsync(auth, statements, session, cancellationToken),
            BackendUnavailable unavailable => SqlRefusal.From(unavailable),
        };
    }

    private async ValueTask<EnforcementOutcome> DenyMissingRulesAsync(
        RulesAuth auth, IReadOnlyList<PlannedStatement> statements, ISqlDatabaseSession? session, CancellationToken cancellationToken)
    {
        var checkedEmpty = await RewriteAllAsync(new RulesSet(new Dictionary<string, RulesTable>()), "", auth, statements, session, cancellationToken);
        return checkedEmpty switch
        {
            SqlRefusal refusal => refusal,
            EnforcedSql => SqlRefusal.RuleDenied("the database has no rules, so end users are denied", 0),
        };
    }

    private async ValueTask<EnforcementOutcome> RewriteAllAsync(
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

    public async ValueTask<StatementEnforcement> RewriteAsync(
        RulesSet rules, RulesAuth auth, PlannedStatement planned, int index, ISqlDatabaseSession? session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(planned);
        if (planned.Kind is StatementKind.Schema or StatementKind.Begin or StatementKind.Commit or StatementKind.Rollback)
        {
            return SqlRefusal.RuleDenied("end users cannot change the schema or control transactions", index);
        }

        return Parse(planned.Statement.Sql, index) switch
        {
            EndUserStatement statement => await EnforceAsync(rules, auth, planned, statement, index, session, cancellationToken),
            SqlRefusal refusal => refusal,
        };
    }

    private async ValueTask<StatementEnforcement> EnforceAsync(
        RulesSet rules, RulesAuth auth, PlannedStatement planned, EndUserStatement statement, int index, ISqlDatabaseSession? session, CancellationToken cancellationToken)
    {
        var main = statement.Tokens[statement.Main];
        if (main.Is("PRAGMA"))
        {
            return SqlRefusal.RuleDenied("end users cannot run PRAGMA statements", index);
        }

        return planned.Kind switch
        {
            StatementKind.Read when main.Is("SELECT") || main.Is("VALUES") =>
                await ReadsAsync(rules, auth, statement, index, skipTarget: -1, session, cancellationToken) switch
                {
                    RewrittenSql text => planned.Statement with { Sql = Join(text.Pieces), Scope = new SqlRuleScope(text.Tables, statement.Ctes, text.Guards, null) },
                    SqlRefusal refusal => refusal,
                },
            StatementKind.Read => SqlRefusal.RuleDenied("end users run SELECT, UPDATE and DELETE statements", index),
            StatementKind.Write => await WriteAsync(rules, auth, planned, statement, index, session, cancellationToken),
            StatementKind.Schema or StatementKind.Begin or StatementKind.Commit or StatementKind.Rollback =>
                SqlRefusal.RuleDenied("end users cannot change the schema or control transactions", index),
            _ => throw new UnreachableException(),
        };
    }

    private async ValueTask<StatementEnforcement> WriteAsync(
        RulesSet rules, RulesAuth auth, PlannedStatement planned, EndUserStatement statement, int index, ISqlDatabaseSession? session, CancellationToken cancellationToken)
    {
        var tokens = statement.Tokens;
        var verb = tokens[statement.Main];
        if (!verb.Is("UPDATE") && !verb.Is("DELETE"))
        {
            return SqlRefusal.RuleDenied("end users insert rows through the rows API, which checks the create rule", index);
        }

        if (tokens.Any(static token => token.Is("RETURNING")))
        {
            return SqlRefusal.RuleDenied("end users cannot use RETURNING; read the changed rows with SELECT", index);
        }

        var deleting = verb.Is("DELETE");
        var targetAt = statement.Main + 1;
        if (targetAt < tokens.Count && tokens[targetAt].Is("OR"))
        {
            return SqlRefusal.RuleDenied("end users cannot use OR conflict clauses such as OR REPLACE or OR IGNORE", index);
        }

        if (deleting && targetAt < tokens.Count && tokens[targetAt].Is("FROM"))
        {
            targetAt++;
        }

        if (targetAt >= tokens.Count || !tokens[targetAt].IsName)
        {
            return SqlRefusal.RuleDenied("the statement's target table could not be read", index);
        }

        if (targetAt + 1 < tokens.Count && tokens[targetAt + 1].Kind == SqlTokenKind.Dot)
        {
            if (!IsMain(tokens[targetAt]) || targetAt + 2 >= tokens.Count || !tokens[targetAt + 2].IsName)
            {
                return SqlRefusal.RuleDenied("end users use only tables of the main database", index);
            }

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
            && columns.FirstOrDefault(column => !writable.Contains(column, AsciiIgnoreCaseComparer.Instance)) is { } blocked)
        {
            return new SqlRefusal("column_not_writable", string.Concat(blocked, " is not a writable column of ", table), "42501", index, StatusCodes.Status403Forbidden,
                Details: new Dictionary<string, string>(StringComparer.Ordinal) { ["column"] = blocked });
        }

        var compiled = RulesCompiler.Compile(condition, null, operation) switch
        {
            CompiledCondition ok when !ok.UsesNew => ok,
            CompiledCondition or InvalidInput => null,
        };
        if (compiled is null)
        {
            return SqlRefusal.RuleDenied(string.Concat("the ", deleting ? "delete" : "update", " rule of ", table, " cannot run on SQL statements; use the rows API"), index,
                deleting ? "delete" : "update", table);
        }

        IReadOnlyList<string>? effective = rule.Read is null ? [] : rule.Columns is { Count: > 0 } listed ? listed : null;
        var targetLive = session is null ? null : await LiveTableAsync(session, table, cancellationToken);
        if (Unreadable(tokens, targetAt, effective, targetLive, AssignedPositions(tokens, targetAt)) is { } hidden)
        {
            return SqlRefusal.ColumnNotReadable(hidden, index);
        }

        HashSet<string>? targetVisible = null;
        if (effective is not null)
        {
            targetVisible = new(effective, AsciiIgnoreCaseComparer.Instance);
            targetVisible.UnionWith(RowColumns(compiled));
            if (RowIdKeys(tokens, targetAt, targetLive).Count > 0)
            {
                targetVisible.Add("rowid");
            }
        }

        var readableColumns = effective?.ToFrozenSet(AsciiIgnoreCaseComparer.Instance);
        var target = new SqlTargetScope(table, targetVisible?.ToFrozenSet(AsciiIgnoreCaseComparer.Instance), readableColumns,
            rule.Writable?.ToFrozenSet(AsciiIgnoreCaseComparer.Instance));
        var predicate = RulesCompiler.Render(compiled, auth, time.GetUtcNow().ToUnixTimeMilliseconds());
        var rewrittenReads = await ReadsAsync(rules, auth, statement, index, skipTarget: targetAt, session, cancellationToken);
        return rewrittenReads switch
        {
            RewrittenSql text => planned.Statement with
            {
                Sql = AppendWhere(tokens, statement.Main, text.Pieces, predicate),
                Scope = new SqlRuleScope(text.Tables, statement.Ctes, text.Guards, target),
            },
            SqlRefusal refusal => refusal,
        };
    }

    private async ValueTask<ReadConditionOutcome> ReadsAsync(
        RulesSet rules, RulesAuth auth, EndUserStatement statement, int index, int skipTarget, ISqlDatabaseSession? session, CancellationToken cancellationToken)
    {
        var tokens = statement.Tokens;
        var pieces = tokens.Select(token => Render(statement.Sql, token)).ToArray();
        Dictionary<string, string> guards = new(AsciiIgnoreCaseComparer.Instance);
        Dictionary<string, string> guardOf = new(AsciiIgnoreCaseComparer.Instance);
        Dictionary<string, SqlTableScope> scoped = new(AsciiIgnoreCaseComparer.Instance);
        Dictionary<string, IReadOnlyList<string>?> keyed = new(AsciiIgnoreCaseComparer.Instance);
        List<RuleSource> sources = [];
        List<string> definitions = [];
        var depth = 0;
        Stack<(int Depth, int Group)> fromDepths = [];
        var expectTable = false;
        for (var at = 0; at < tokens.Count; at++)
        {
            var token = tokens[at];
            switch (token.Kind)
            {
                case SqlTokenKind.Open:
                    depth++;
                    if (expectTable && !(at + 1 < tokens.Count && (tokens[at + 1].Is("SELECT") || tokens[at + 1].Is("VALUES") || tokens[at + 1].Is("WITH"))))
                    {
                        fromDepths.Push((depth, at));
                        continue;
                    }

                    if (expectTable)
                    {
                        sources.Add(new RuleSource(Group(fromDepths), null, ""));
                    }

                    expectTable = false;
                    continue;
                case SqlTokenKind.Close:
                    depth--;
                    while (fromDepths.Count > 0 && fromDepths.Peek().Depth > depth)
                    {
                        fromDepths.Pop();
                    }

                    expectTable = false;
                    continue;
            }

            if (token.Is("IN") && at + 1 < tokens.Count && (tokens[at + 1].Kind == SqlTokenKind.String
                || (tokens[at + 1].IsName && !(at + 2 < tokens.Count && tokens[at + 2].Kind == SqlTokenKind.Open))))
            {
                return SqlRefusal.RuleDenied("end users write IN (SELECT ...) instead of IN followed by a table name", index);
            }

            if (expectTable && token.Kind == SqlTokenKind.String)
            {
                return SqlRefusal.RuleDenied("end users name tables without single quotes", index);
            }

            if (!expectTable || !token.IsName)
            {
                expectTable = false;
                if (token.Is("FROM") || token.Is("JOIN"))
                {
                    if (token.Is("FROM"))
                    {
                        fromDepths.Push((depth, at));
                    }

                    expectTable = true;
                }
                else if (token.Kind == SqlTokenKind.Comma && fromDepths.Count > 0 && fromDepths.Peek().Depth == depth)
                {
                    expectTable = true;
                }
                else if (token.Kind == SqlTokenKind.Word && ClauseEnds.Contains(token.Value) && fromDepths.Count > 0 && fromDepths.Peek().Depth == depth
                    && !JoinWords.Contains(token.Value))
                {
                    fromDepths.Pop();
                }

                continue;
            }

            expectTable = false;
            var start = at;
            var qualified = false;
            if (at + 1 < tokens.Count && tokens[at + 1].Kind == SqlTokenKind.Dot)
            {
                if (!IsMain(token) || at + 2 >= tokens.Count || !tokens[at + 2].IsName)
                {
                    return SqlRefusal.RuleDenied("end users read only tables of the main database", index);
                }

                at += 2;
                qualified = true;
            }

            if (at + 1 < tokens.Count && tokens[at + 1].Kind == SqlTokenKind.Open)
            {
                if (qualified || !TableFunctions.Contains(tokens[at].Value))
                {
                    return SqlRefusal.RuleDenied(string.Concat("end users cannot read ", tokens[at].Value), index, "read", tokens[at].Value);
                }

                sources.Add(new RuleSource(Group(fromDepths), null, ""));
                continue;
            }

            var table = tokens[at].Value;
            var aliased = at + 1 < tokens.Count && (tokens[at + 1].Is("AS") || tokens[at + 1].Kind == SqlTokenKind.String
                || (tokens[at + 1].IsName && !(tokens[at + 1].Kind == SqlTokenKind.Word && ClauseEnds.Contains(tokens[at + 1].Value))));
            var aliasAt = !aliased ? -1 : tokens[at + 1].Is("AS") ? at + 2 : at + 1;
            var alias = aliasAt >= 0 && aliasAt < tokens.Count ? tokens[aliasAt].Value : table;
            if (at == skipTarget || (!qualified && statement.Ctes.Contains(table)))
            {
                sources.Add(new RuleSource(Group(fromDepths), null, alias));
                continue;
            }

            if (rules.Table(table) is not { Read: { } read } rule)
            {
                return SqlRefusal.RuleDenied(string.Concat("the rules do not allow reading ", table), index, "read", table);
            }

            var live = session is null ? null : await LiveTableAsync(session, table, cancellationToken);
            var liveNames = live?.Declared;
            if (liveNames is not null && rule.Columns is { Count: > 0 } listed && listed.Any(column => !liveNames.Contains(column, AsciiIgnoreCaseComparer.Instance)))
            {
                return SqlRefusal.RulesInvalid(string.Concat("the read columns of ", table, " reference a missing column; deploy the rules again"), index);
            }

            var compiled = RulesCompiler.Compile(read, liveNames, RulesOperation.Read) switch
            {
                CompiledCondition condition => condition,
                InvalidInput => null,
            };
            if (compiled is null)
            {
                return SqlRefusal.RulesInvalid(string.Concat("the read rule of ", table, " no longer compiles; deploy the rules again"), index);
            }

            var readable = rule.Columns is { Count: > 0 } listedColumns ? listedColumns : null;
            if (Unreadable(tokens, at, readable, live, FrozenSet<int>.Empty) is { } hiddenColumn)
            {
                return SqlRefusal.ColumnNotReadable(hiddenColumn, index);
            }

            if (!guardOf.TryGetValue(table, out var guard))
            {
                guard = string.Concat(GuardPrefix, guards.Count.ToString(CultureInfo.InvariantCulture));
                guards[guard] = table;
                guardOf[table] = guard;
                var keys = RowIdKeys(tokens, at, live);
                if (keys.Count > 0)
                {
                    keyed[table] = readable ?? live?.Names;
                }

                var columns = string.Join(", ", [.. keys.Select(key => string.Concat(live?.RowIdSource ?? key, " AS ", RulesCompiler.Quote(key))),
                    .. readable is null ? ["*"] : readable.Select(RulesCompiler.Quote)]);
                definitions.Add(string.Concat(RulesCompiler.Quote(guard), " AS (SELECT ", columns, " FROM ", RulesCompiler.Quote(table), " WHERE ",
                    RulesCompiler.Render(compiled, auth, time.GetUtcNow().ToUnixTimeMilliseconds()), ")"));
                HashSet<string>? visible = null;
                if (readable is not null)
                {
                    visible = new(readable, AsciiIgnoreCaseComparer.Instance);
                    visible.UnionWith(RowColumns(compiled));
                    if (keys.Count > 0)
                    {
                        visible.Add("rowid");
                    }
                }

                scoped[table] = new SqlTableScope(visible?.ToFrozenSet(AsciiIgnoreCaseComparer.Instance));
            }

            sources.Add(new RuleSource(Group(fromDepths), table, alias));
            pieces[start] = aliased ? RulesCompiler.Quote(guard) : string.Concat(RulesCompiler.Quote(guard), " AS ", RulesCompiler.Quote(table));
            for (var consumed = start + 1; consumed <= at; consumed++)
            {
                pieces[consumed] = "";
            }
        }

        if (keyed.Count > 0 && ExpandWildcards(tokens, pieces, sources, keyed) is { } wildcard)
        {
            return SqlRefusal.RuleDenied(wildcard, index);
        }

        if (definitions.Count > 0)
        {
            var guardList = string.Join(", ", definitions);
            if (tokens[statement.Start].Is("WITH"))
            {
                var firstCte = statement.Start + (tokens[statement.Start + 1].Is("RECURSIVE") ? 2 : 1);
                pieces[firstCte] = string.Concat(guardList, ", ", pieces[firstCte]);
            }
            else
            {
                pieces[statement.Start] = string.Concat("WITH ", guardList, " ", pieces[statement.Start]);
            }
        }

        return new RewrittenSql(pieces, guards.ToFrozenDictionary(AsciiIgnoreCaseComparer.Instance), scoped.ToFrozenDictionary(AsciiIgnoreCaseComparer.Instance));
    }

    private static EndUserStatementOutcome Parse(string sql, int index)
    {
        if (sql.Contains('\0', StringComparison.Ordinal))
        {
            return SqlRefusal.RuleDenied("end-user SQL cannot contain NUL characters", index);
        }

        if (!SqlText.TryTokenize(sql, out var tokens) || tokens.Count == 0)
        {
            return SqlRefusal.RuleDenied("the statement could not be read", index);
        }

        var cursor = 0;
        foreach (var token in tokens)
        {
            if (!IsBlank(sql.AsSpan(cursor, token.Start - cursor)))
            {
                return SqlRefusal.RuleDenied("end-user SQL cannot contain comments", index);
            }

            cursor = token.End;
        }

        if (!IsBlank(sql.AsSpan(cursor)))
        {
            return SqlRefusal.RuleDenied("end-user SQL cannot contain comments", index);
        }

        var meaningful = tokens[^1].Kind == SqlTokenKind.Semicolon ? tokens[..^1] : tokens;
        if (meaningful.Count == 0 || meaningful.Any(static token => token.Kind == SqlTokenKind.Semicolon))
        {
            return SqlRefusal.RuleDenied("end users send exactly one statement per entry", index);
        }

        return Shape(sql, meaningful, index);
    }

    private static EndUserStatementOutcome Shape(string sql, List<SqlToken> tokens, int index)
    {
        var start = tokens[0].Is("EXPLAIN") ? (tokens.Count > 2 && tokens[1].Is("QUERY") && tokens[2].Is("PLAN") ? 3 : 1) : 0;
        var at = start;
        HashSet<string> ctes = new(AsciiIgnoreCaseComparer.Instance);
        if (at < tokens.Count && tokens[at].Is("WITH"))
        {
            at++;
            if (at < tokens.Count && tokens[at].Is("RECURSIVE"))
            {
                at++;
            }

            while (true)
            {
                if (at >= tokens.Count || !tokens[at].IsName)
                {
                    return UnreadableWith(index);
                }

                if (tokens[at].Value.StartsWith("_tb_", StringComparison.OrdinalIgnoreCase))
                {
                    return SqlRefusal.RuleDenied("common table expression names starting with _tb_ are reserved", index);
                }

                ctes.Add(tokens[at].Value);
                at++;
                if (at < tokens.Count && tokens[at].Kind == SqlTokenKind.Open)
                {
                    at = AfterGroup(tokens, at);
                }

                if (at < 0 || at >= tokens.Count || !tokens[at].Is("AS"))
                {
                    return UnreadableWith(index);
                }

                at++;
                if (at < tokens.Count && tokens[at].Is("NOT"))
                {
                    at++;
                }

                if (at < tokens.Count && tokens[at].Is("MATERIALIZED"))
                {
                    at++;
                }

                if (at >= tokens.Count || tokens[at].Kind != SqlTokenKind.Open)
                {
                    return UnreadableWith(index);
                }

                at = AfterGroup(tokens, at);
                if (at < 0)
                {
                    return UnreadableWith(index);
                }

                if (at < tokens.Count && tokens[at].Kind == SqlTokenKind.Comma)
                {
                    at++;
                    continue;
                }

                break;
            }
        }

        if (at >= tokens.Count || tokens[at].Kind != SqlTokenKind.Word)
        {
            return SqlRefusal.RuleDenied("the statement could not be read", index);
        }

        for (var position = 0; position < tokens.Count; position++)
        {
            if (position != start && tokens[position].Is("WITH"))
            {
                return SqlRefusal.RuleDenied("end users declare common table expressions only at the start of the statement", index);
            }
        }

        return new EndUserStatement(sql, tokens, start, at, ctes.ToFrozenSet(AsciiIgnoreCaseComparer.Instance));
    }

    private static SqlRefusal UnreadableWith(int index) => SqlRefusal.RuleDenied("the WITH clause could not be read", index);

    private static int AfterGroup(List<SqlToken> tokens, int open)
    {
        var depth = 0;
        for (var at = open; at < tokens.Count; at++)
        {
            depth += tokens[at].Kind switch
            {
                SqlTokenKind.Open => 1,
                SqlTokenKind.Close => -1,
                SqlTokenKind.Word or SqlTokenKind.Quoted or SqlTokenKind.String or SqlTokenKind.Blob or SqlTokenKind.Number or SqlTokenKind.Parameter
                    or SqlTokenKind.Semicolon or SqlTokenKind.Dot or SqlTokenKind.Comma or SqlTokenKind.Symbol => 0,
                _ => throw new UnreachableException(),
            };
            if (depth == 0)
            {
                return at + 1;
            }
        }

        return -1;
    }

    private static bool IsBlank(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (!SqlText.IsSpace(character) && character != '\uFEFF')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsMain(SqlToken token) => token.IsName && AsciiIgnoreCaseComparer.Instance.Equals(token.Value, "main");

    private static string Render(string sql, SqlToken token) => token.Kind switch
    {
        SqlTokenKind.Word or SqlTokenKind.Number or SqlTokenKind.Parameter or SqlTokenKind.Semicolon or SqlTokenKind.Dot or SqlTokenKind.Open
            or SqlTokenKind.Close or SqlTokenKind.Comma or SqlTokenKind.Symbol => token.Value,
        SqlTokenKind.String => RulesCompiler.Literal(token.Value),
        SqlTokenKind.Blob => string.Concat("X", RulesCompiler.Literal(token.Value)),
        SqlTokenKind.Quoted => sql[token.Start] switch
        {
            '[' => string.Concat("[", token.Value, "]"),
            '`' => string.Concat("`", token.Value.Replace("`", "``", StringComparison.Ordinal), "`"),
            _ => RulesCompiler.Quote(token.Value),
        },
        _ => throw new UnreachableException(),
    };

    private static string Join(IEnumerable<string> pieces) => string.Join(' ', pieces.Where(static piece => piece.Length > 0));

    private static IEnumerable<string> RowColumns(CompiledCondition condition)
    {
        foreach (var part in condition.Parts)
        {
            var text = part switch
            {
                string literal => literal,
                ConditionParameter => null,
            };
            if (text is not null && text.StartsWith("row.\"", StringComparison.Ordinal) && text.EndsWith('"'))
            {
                yield return text[5..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
            }
        }
    }

    private static async ValueTask<LiveTable?> LiveTableAsync(ISqlDatabaseSession session, string table, CancellationToken cancellationToken)
    {
        var name = RulesCompiler.Literal(table);
        var request = new SqlStatementRequest(session.DatabaseId,
            [
                new SqlStatement(string.Concat("SELECT name, type, pk, hidden FROM pragma_table_xinfo(", name, ")"), [], SqlBuild.NoNamed),
                new SqlStatement(string.Concat("SELECT type, wr FROM pragma_table_list(", name, ") WHERE schema = 'main'"), [], SqlBuild.NoNamed),
                new SqlStatement(string.Concat("SELECT count(*) FROM pragma_index_list(", name, ") WHERE origin = 'pk'"), [], SqlBuild.NoNamed),
            ],
            SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty);
        var outcome = await session.ExecuteReadAsync(request, cancellationToken);
        IReadOnlyList<SqlResultSet> results = outcome switch
        {
            SqlReadResult read => read.ResultSets,
            SqlError or StorageBusy or StorageTimeout => [],
        };
        if (results is not [{ Rows: { Count: > 0 } columns }, { Rows: [var kind] }, { Rows: [var keyIndexes] }])
        {
            return null;
        }

        var hasRowId = SqlBuild.Format(kind[0]) == "table" && kind[1].TryGetIntegral(out var withoutRowId) && withoutRowId == 0;
        var declared = columns.Select(static row => SqlBuild.Format(row[0])).ToArray();
        var keys = columns.Where(static row => row[2].TryGetIntegral(out var key) && key > 0).ToList();
        var alias = hasRowId && keys is [var only] && string.Equals(SqlBuild.Format(only[1]), "INTEGER", StringComparison.OrdinalIgnoreCase)
            && keyIndexes[0].TryGetIntegral(out var pkIndexes) && pkIndexes == 0
            ? SqlBuild.Format(only[0])
            : null;
        var source = hasRowId ? RowIdOrder.FirstOrDefault(implicitName => !declared.Contains(implicitName, AsciiIgnoreCaseComparer.Instance)) : null;
        string[] names = [.. columns.Where(static row => !(row[3].TryGetIntegral(out var hidden) && hidden == 1)).Select(static row => SqlBuild.Format(row[0]))];
        return new LiveTable(names, declared, alias, source);
    }

    private static readonly string[] RowIdOrder = ["rowid", "_rowid_", "oid"];

    private static int Group(Stack<(int Depth, int Group)> fromDepths) => fromDepths.Count > 0 ? fromDepths.Peek().Group : -1;

    private static bool IsRowIdReference(IReadOnlyList<SqlToken> tokens, int at) =>
        tokens[at].IsName && RowIdNames.Contains(tokens[at].Value) && !(at > 0 && tokens[at - 1].Is("AS"))
        && !(at + 1 < tokens.Count && tokens[at + 1].Kind is SqlTokenKind.Open or SqlTokenKind.Dot);

    private static List<string> RowIdKeys(IReadOnlyList<SqlToken> tokens, int tableAt, LiveTable? live)
    {
        List<string> keys = [];
        for (var at = 0; at < tokens.Count; at++)
        {
            if (at == tableAt || !IsRowIdReference(tokens, at))
            {
                continue;
            }

            var name = RowIdOrder.First(implicitName => AsciiIgnoreCaseComparer.Instance.Equals(implicitName, tokens[at].Value));
            if (live?.Declared.Contains(name, AsciiIgnoreCaseComparer.Instance) != true && !keys.Contains(name, AsciiIgnoreCaseComparer.Instance))
            {
                keys.Add(name);
            }
        }

        keys.Sort(static (left, right) => Array.IndexOf(RowIdOrder, left).CompareTo(Array.IndexOf(RowIdOrder, right)));
        return keys;
    }

    private static string? Unreadable(IReadOnlyList<SqlToken> tokens, int tableAt, IReadOnlyList<string>? visibleColumns, LiveTable? live, IReadOnlySet<int> written)
    {
        for (var at = 0; at < tokens.Count; at++)
        {
            if (at == tableAt || !IsRowIdReference(tokens, at))
            {
                continue;
            }

            var name = tokens[at].Value;
            if (live is not null && live.Declared.Contains(name, AsciiIgnoreCaseComparer.Instance))
            {
                if (visibleColumns is not null && !visibleColumns.Contains(name, AsciiIgnoreCaseComparer.Instance))
                {
                    return name;
                }

                continue;
            }

            if (live is not null && live.RowIdSource is null)
            {
                return name;
            }

            if (visibleColumns is not null && (live is null
                || (live.RowIdAlias is { } alias && !visibleColumns.Contains(alias, AsciiIgnoreCaseComparer.Instance))
                || (live.Declared.Contains("rowid", AsciiIgnoreCaseComparer.Instance) && !visibleColumns.Contains("rowid", AsciiIgnoreCaseComparer.Instance))))
            {
                return name;
            }
        }

        return visibleColumns is null || live is null ? null : FindHiddenColumn(tokens, tableAt, live.Declared, visibleColumns, written);
    }

    private static string? ExpandWildcards(
        IReadOnlyList<SqlToken> tokens, string[] pieces, List<RuleSource> sources, Dictionary<string, IReadOnlyList<string>?> keyed)
    {
        for (var at = 0; at < tokens.Count; at++)
        {
            if (tokens[at] is not { Kind: SqlTokenKind.Symbol, Value: "*" } || at == 0)
            {
                continue;
            }

            var previous = tokens[at - 1];
            var qualified = previous.Kind == SqlTokenKind.Dot && at >= 2 && tokens[at - 2].IsName;
            if (!qualified && !(previous.Is("SELECT") || previous.Is("DISTINCT") || previous.Is("ALL") || previous.Kind == SqlTokenKind.Comma))
            {
                continue;
            }

            var group = FromOf(tokens, at);
            var members = sources.Where(source => source.Group == group).ToList();
            if (qualified)
            {
                if (members.FirstOrDefault(source => source.Table is not null && AsciiIgnoreCaseComparer.Instance.Equals(source.Alias, tokens[at - 2].Value)) is { Table: { } table } named
                    && keyed.TryGetValue(table, out var namedColumns))
                {
                    if (namedColumns is null)
                    {
                        return string.Concat("list the columns of ", table, " instead of * when reading rowid");
                    }

                    pieces[at - 2] = Columns(named.Alias, namedColumns);
                    pieces[at - 1] = "";
                    pieces[at] = "";
                }

                continue;
            }

            if (!members.Any(source => source.Table is not null && keyed.ContainsKey(source.Table)))
            {
                continue;
            }

            if (members.Any(source => source.Table is null) || tokens.Any(static token => token.Is("NATURAL") || token.Is("USING")))
            {
                return "list the columns instead of * when reading rowid";
            }

            List<string> expanded = [];
            foreach (var member in members)
            {
                IReadOnlyList<string>? memberColumns = keyed.TryGetValue(member.Table ?? "", out var keyedColumns) ? keyedColumns : null;
                if (memberColumns is null)
                {
                    return "list the columns instead of * when reading rowid";
                }

                expanded.Add(Columns(member.Alias, memberColumns));
            }

            pieces[at] = string.Join(", ", expanded);
        }

        return null;
    }

    private static string Columns(string alias, IReadOnlyList<string> columns) =>
        string.Join(", ", columns.Select(column => string.Concat(RulesCompiler.Quote(alias), ".", RulesCompiler.Quote(column))));

    private static int FromOf(IReadOnlyList<SqlToken> tokens, int at)
    {
        var depth = 0;
        for (var next = at + 1; next < tokens.Count; next++)
        {
            switch (tokens[next].Kind)
            {
                case SqlTokenKind.Open:
                    depth++;
                    continue;
                case SqlTokenKind.Close when depth == 0:
                    return -2;
                case SqlTokenKind.Close:
                    depth--;
                    continue;
            }

            if (depth == 0 && tokens[next].Is("FROM"))
            {
                return next;
            }
        }

        return -2;
    }

    private static FrozenSet<int> AssignedPositions(IReadOnlyList<SqlToken> tokens, int targetAt)
    {
        HashSet<int> positions = [];
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
            return positions.ToFrozenSet();
        }

        var depth = 0;
        var listing = false;
        for (var at = setAt + 1; at < tokens.Count; at++)
        {
            var token = tokens[at];
            if (token.Kind == SqlTokenKind.Open)
            {
                listing = depth == 0 && (at == setAt + 1 || tokens[at - 1].Kind == SqlTokenKind.Comma);
                depth++;
            }
            else if (token.Kind == SqlTokenKind.Close)
            {
                listing = false;
                depth--;
            }
            else if (listing && (token.IsName || token.Kind == SqlTokenKind.String))
            {
                positions.Add(at);
            }
            else if (depth == 0 && (token.Is("WHERE") || token.Is("FROM") || token.Is("RETURNING")))
            {
                break;
            }
            else if (depth == 0 && (token.IsName || token.Kind == SqlTokenKind.String) && at + 1 < tokens.Count && tokens[at + 1] is { Kind: SqlTokenKind.Symbol, Value: "=" })
            {
                positions.Add(at);
            }
        }

        return positions.ToFrozenSet();
    }

    private static string? FindHiddenColumn(IReadOnlyList<SqlToken> tokens, int tableAt, IReadOnlyList<string> liveNames, IReadOnlyList<string> visibleColumns, IReadOnlySet<int> written)
    {
        Dictionary<string, string>? hidden = null;
        foreach (var name in liveNames)
        {
            if (!visibleColumns.Contains(name, AsciiIgnoreCaseComparer.Instance))
            {
                hidden ??= new(AsciiIgnoreCaseComparer.Instance);
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
            if (!token.IsName || at == tableAt || written.Contains(at))
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

    private static string AppendWhere(IReadOnlyList<SqlToken> tokens, int main, IReadOnlyList<string> pieces, string predicate)
    {
        var depth = 0;
        var whereAt = -1;
        var tailAt = -1;
        for (var at = main; at < tokens.Count; at++)
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

        var end = tailAt >= 0 ? tailAt : pieces.Count;
        IEnumerable<string> rewritten = whereAt < 0
            ? [.. pieces.Take(end), "WHERE", predicate, .. pieces.Skip(end)]
            : [.. pieces.Take(whereAt + 1), "(", .. pieces.Take(end).Skip(whereAt + 1), ") AND", predicate, .. pieces.Skip(end)];
        return Join(rewritten);
    }

    private static List<string> Assigned(IReadOnlyList<SqlToken> tokens, int targetAt) =>
        [.. AssignedPositions(tokens, targetAt).Order().Select(position => tokens[position].Value)];

    private static SqlRefusal? Add(List<SqlStatement> statements, SqlStatement statement)
    {
        statements.Add(statement);
        return null;
    }
}
