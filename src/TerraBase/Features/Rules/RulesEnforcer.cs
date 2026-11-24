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

internal readonly union SchemaObjectsOutcome(SchemaObjects, SqlRefusal);

internal sealed record SchemaObjects(FrozenSet<string> Views, FrozenSet<string> Triggers);

internal sealed record EnforcedSql(IReadOnlyList<SqlStatement> Statements, string? RulesVersion);

internal sealed record RewrittenSql(
    IReadOnlyList<string> Pieces, IReadOnlyDictionary<string, string> Guards, IReadOnlyDictionary<string, SqlTableScope> Tables, IReadOnlySet<string> Views);

internal sealed record EndUserStatement(string Sql, IReadOnlyList<SqlToken> Tokens, int Start, int Main, FrozenSet<string> Ctes);

internal sealed class RulesEnforcer(RulesStore store, TimeProvider time)
{
    private static readonly FrozenSet<string> ClauseEnds = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "WHERE", "GROUP", "ORDER", "LIMIT", "HAVING", "WINDOW", "UNION", "EXCEPT", "INTERSECT", "ON", "USING", "RETURNING", "SET", "VALUES",
        "JOIN", "LEFT", "RIGHT", "FULL", "INNER", "CROSS", "NATURAL", "OUTER", "OFFSET");

    private static readonly FrozenSet<string> JoinWords = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "LEFT", "RIGHT", "FULL", "INNER", "CROSS", "NATURAL", "OUTER", "ON", "USING");

    private const string GuardPrefix = "_tb_rule_";

    private static readonly FrozenSet<string> TailClauses = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "RETURNING", "ORDER", "LIMIT");

    private static readonly FrozenSet<string> RowIdNames = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "rowid", "_rowid_", "oid");

    private static readonly FrozenSet<string> TableFunctions = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "json_each", "json_tree");

    private static readonly FrozenSet<string> SqlKeywords = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
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
                    RewrittenSql text => planned.Statement with { Sql = Join(text.Pieces), Scope = new SqlRuleScope(text.Tables, statement.Ctes, text.Guards, text.Views, null) },
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
            && columns.FirstOrDefault(column => !writable.Contains(column, StringComparer.OrdinalIgnoreCase)) is { } blocked)
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

        var restricted = rule.Columns is { Count: > 0 } listed ? listed : null;
        var targetNames = session is null ? null : await LiveColumnsAsync(session, table, cancellationToken);
        if (Unreadable(tokens, targetAt, restricted, targetNames) is { } hidden)
        {
            return SqlRefusal.ColumnNotReadable(hidden, index);
        }

        HashSet<string>? targetVisible = null;
        if (restricted is not null || rule.Read is null)
        {
            targetVisible = new(restricted ?? [], StringComparer.OrdinalIgnoreCase);
            targetVisible.UnionWith(RowColumns(compiled));
        }

        var readableColumns = restricted is not null ? restricted.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
            : rule.Read is null ? FrozenSet<string>.Empty : null;
        var target = new SqlTargetScope(table, targetVisible?.ToFrozenSet(StringComparer.OrdinalIgnoreCase), readableColumns,
            rule.Writable?.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
        var predicate = RulesCompiler.Render(compiled, auth, time.GetUtcNow().ToUnixTimeMilliseconds());
        var rewrittenReads = await ReadsAsync(rules, auth, statement, index, skipTarget: targetAt, session, cancellationToken);
        return rewrittenReads switch
        {
            RewrittenSql text => planned.Statement with
            {
                Sql = AppendWhere(tokens, statement.Main, text.Pieces, predicate),
                Scope = new SqlRuleScope(text.Tables, statement.Ctes, text.Guards, text.Views, target),
            },
            SqlRefusal refusal => refusal,
        };
    }

    private async ValueTask<ReadConditionOutcome> ReadsAsync(
        RulesSet rules, RulesAuth auth, EndUserStatement statement, int index, int skipTarget, ISqlDatabaseSession? session, CancellationToken cancellationToken)
    {
        var tokens = statement.Tokens;
        var pieces = tokens.Select(token => Render(statement.Sql, token)).ToArray();
        Dictionary<string, string> guards = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> guardOf = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SqlTableScope> scoped = new(StringComparer.OrdinalIgnoreCase);
        List<string> definitions = [];
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
                    if (expectTable && !(at + 1 < tokens.Count && (tokens[at + 1].Is("SELECT") || tokens[at + 1].Is("VALUES") || tokens[at + 1].Is("WITH"))))
                    {
                        fromDepths.Push(depth);
                        continue;
                    }

                    expectTable = false;
                    continue;
                case SqlTokenKind.Close:
                    depth--;
                    while (fromDepths.Count > 0 && fromDepths.Peek() > depth)
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
                        fromDepths.Push(depth);
                    }

                    expectTable = true;
                }
                else if (token.Kind == SqlTokenKind.Comma && fromDepths.Count > 0 && fromDepths.Peek() == depth)
                {
                    expectTable = true;
                }
                else if (token.Kind == SqlTokenKind.Word && ClauseEnds.Contains(token.Value) && fromDepths.Count > 0 && fromDepths.Peek() == depth
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

                continue;
            }

            var table = tokens[at].Value;
            if (at == skipTarget || (!qualified && statement.Ctes.Contains(table)))
            {
                continue;
            }

            if (rules.Table(table) is not { Read: { } read } rule)
            {
                return SqlRefusal.RuleDenied(string.Concat("the rules do not allow reading ", table), index, "read", table);
            }

            var liveNames = session is null ? null : await LiveColumnsAsync(session, table, cancellationToken);
            if (liveNames is not null && rule.Columns is { Count: > 0 } listed && listed.Any(column => !liveNames.Contains(column, StringComparer.OrdinalIgnoreCase)))
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
            if (Unreadable(tokens, at, readable, liveNames) is { } hiddenColumn)
            {
                return SqlRefusal.ColumnNotReadable(hiddenColumn, index);
            }

            if (!guardOf.TryGetValue(table, out var guard))
            {
                guard = string.Concat(GuardPrefix, guards.Count.ToString(CultureInfo.InvariantCulture));
                guards[guard] = table;
                guardOf[table] = guard;
                var columns = readable is null ? "*" : string.Join(", ", readable.Select(RulesCompiler.Quote));
                definitions.Add(string.Concat(RulesCompiler.Quote(guard), " AS (SELECT ", columns, " FROM ", RulesCompiler.Quote(table), " WHERE ",
                    RulesCompiler.Render(compiled, auth, time.GetUtcNow().ToUnixTimeMilliseconds()), ")"));
                HashSet<string>? visible = null;
                if (readable is not null)
                {
                    visible = new(readable, StringComparer.OrdinalIgnoreCase);
                    visible.UnionWith(RowColumns(compiled));
                }

                scoped[table] = new SqlTableScope(visible?.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
            }

            var aliased = at + 1 < tokens.Count && (tokens[at + 1].Is("AS") || tokens[at + 1].Kind == SqlTokenKind.String || (tokens[at + 1].IsName && !(tokens[at + 1].Kind == SqlTokenKind.Word && ClauseEnds.Contains(tokens[at + 1].Value))));
            pieces[start] = aliased ? RulesCompiler.Quote(guard) : string.Concat(RulesCompiler.Quote(guard), " AS ", RulesCompiler.Quote(table));
            for (var consumed = start + 1; consumed <= at; consumed++)
            {
                pieces[consumed] = "";
            }
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

        var objects = await SchemaObjectsAsync(session, [.. scoped.Keys], [.. statement.Ctes, .. guards.Keys, .. scoped.Keys], skipTarget >= 0 && statement.Ctes.Count > 0,
            index, cancellationToken);
        return objects switch
        {
            SchemaObjects found when found.Triggers.Overlaps(statement.Ctes) || found.Triggers.Overlaps(guards.Keys) =>
                SqlRefusal.RuleDenied("a common table expression name matches a trigger; rename it", index),
            SchemaObjects found => new RewrittenSql(pieces, guards.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), scoped.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
                found.Views.Except(found.Triggers, StringComparer.OrdinalIgnoreCase).ToFrozenSet(StringComparer.OrdinalIgnoreCase)),
            SqlRefusal refusal => refusal,
        };
    }

    private static async ValueTask<SchemaObjectsOutcome> SchemaObjectsAsync(
        ISqlDatabaseSession? session, string[] tables, string[] names, bool triggersRequired, int index, CancellationToken cancellationToken)
    {
        var none = new SchemaObjects(FrozenSet<string>.Empty, FrozenSet<string>.Empty);
        if (session is null)
        {
            return triggersRequired ? SqlRefusal.RuleDenied("end-user writes in a transaction cannot declare common table expressions", index) : none;
        }

        if (names.Length == 0)
        {
            return none;
        }

        var sql = string.Concat("SELECT type, name FROM sqlite_schema WHERE (type = 'view' AND name COLLATE NOCASE IN (", Literals(tables),
            ")) OR (type = 'trigger' AND name COLLATE NOCASE IN (", Literals(names), "))");
        var request = new SqlStatementRequest(session.DatabaseId, [new SqlStatement(sql, [], SqlBuild.NoNamed)], SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty);
        var outcome = await session.ExecuteReadAsync(request, cancellationToken);
        return outcome switch
        {
            SqlReadResult { ResultSets: [.., var last] } => new SchemaObjects(
                last.Rows.Where(static row => SqlBuild.Format(row[0]) == "view").Select(static row => SqlBuild.Format(row[1])).ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                last.Rows.Where(static row => SqlBuild.Format(row[0]) == "trigger").Select(static row => SqlBuild.Format(row[1])).ToFrozenSet(StringComparer.OrdinalIgnoreCase)),
            SqlReadResult => SqlRefusal.RuleDenied("the schema could not be checked", index),
            SqlError error => SqlRefusal.From(error),
            StorageBusy busy => SqlRefusal.From(busy),
            StorageTimeout timeout => SqlRefusal.From(timeout),
        };
    }

    private static string Literals(string[] values) => string.Join(", ", values.Select(static value => RulesCompiler.Literal(value)));

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
        HashSet<string> ctes = new(StringComparer.OrdinalIgnoreCase);
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

        return new EndUserStatement(sql, tokens, start, at, ctes.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
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
            if (!char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsMain(SqlToken token) => token.IsName && string.Equals(token.Value, "main", StringComparison.OrdinalIgnoreCase);

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

    private static async ValueTask<IReadOnlyList<string>?> LiveColumnsAsync(ISqlDatabaseSession session, string table, CancellationToken cancellationToken)
    {
        var tableColumns = await RulesService.TableColumnsAsync(session, table, cancellationToken);
        return tableColumns.Count > 0 ? [.. tableColumns.Select(static column => column.Name)] : null;
    }

    private static string? Unreadable(IReadOnlyList<SqlToken> tokens, int tableAt, IReadOnlyList<string>? visibleColumns, IReadOnlyList<string>? liveNames)
    {
        for (var at = 0; at < tokens.Count; at++)
        {
            var token = tokens[at];
            if (at == tableAt || !token.IsName || !RowIdNames.Contains(token.Value) || (at > 0 && tokens[at - 1].Is("AS"))
                || (at + 1 < tokens.Count && tokens[at + 1].Kind is SqlTokenKind.Open or SqlTokenKind.Dot))
            {
                continue;
            }

            var declared = liveNames is not null && liveNames.Contains(token.Value, StringComparer.OrdinalIgnoreCase);
            if (!declared || (visibleColumns is not null && !visibleColumns.Contains(token.Value, StringComparer.OrdinalIgnoreCase)))
            {
                return token.Value;
            }
        }

        return visibleColumns is null || liveNames is null ? null : FindHiddenColumn(tokens, tableAt, liveNames, visibleColumns);
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
        var listing = false;
        for (var at = setAt + 1; at < tokens.Count; at++)
        {
            var token = tokens[at];
            if (token.Kind == SqlTokenKind.Open)
            {
                listing = depth == 0 && (at == setAt + 1 || tokens[at - 1].Kind == SqlTokenKind.Comma);
                depth++;
            }
            else if (listing && token.Kind == SqlTokenKind.Close)
            {
                listing = false;
                depth--;
            }
            else if (listing && (token.IsName || token.Kind == SqlTokenKind.String))
            {
                columns.Add(token.Value);
            }
            else if (token.Kind == SqlTokenKind.Close)
            {
                depth--;
            }
            else if (depth == 0 && (token.Is("WHERE") || token.Is("FROM") || token.Is("RETURNING")))
            {
                break;
            }
            else if (depth == 0 && (token.IsName || token.Kind == SqlTokenKind.String) && at + 1 < tokens.Count && tokens[at + 1] is { Kind: SqlTokenKind.Symbol, Value: "=" })
            {
                columns.Add(token.Value);
            }
        }

        return columns;
    }

    private static SqlRefusal? Add(List<SqlStatement> statements, SqlStatement statement)
    {
        statements.Add(statement);
        return null;
    }
}
