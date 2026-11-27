using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using TerraBase.Engine;
using TerraBase.Features.Sql;
using TerraBase.Hosting;

namespace TerraBase.Features.Rules;

internal readonly union EnforcementOutcome(EnforcedSql, SqlRefusal);

internal readonly union StatementEnforcement(SqlStatement, SqlRefusal);

internal readonly union TemplateBuildOutcome(EnforcedTemplate, SqlRefusal);

internal readonly union ReadTemplateOutcome(ReadTemplate, SqlRefusal);

internal readonly union EndUserStatementOutcome(EndUserStatement, SqlRefusal);

internal sealed record EnforcedSql(IReadOnlyList<SqlStatement> Statements, string? RulesVersion);

internal sealed record EnforcedTemplate(
    string[] StaticChunks,
    CompiledCondition[] Conditions,
    SqlRuleScope Scope,
    bool UsesRequestTime);

internal sealed record ReadTemplate(
    string[] Pieces,
    string[] GuardHeaders,
    CompiledCondition[] GuardConditions,
    IReadOnlyDictionary<string, string> Guards,
    IReadOnlyDictionary<string, SqlTableScope> Tables,
    long? SchemaVersion);

internal delegate ValueTask<SqlReadOutcome> CatalogReader(IReadOnlyList<SqlStatement> statements, CancellationToken cancellationToken);

internal sealed record LiveTable(
    IReadOnlyList<string> Names,
    IReadOnlyList<string> Declared,
    FrozenSet<string> DeclaredSet,
    string? RowIdAlias,
    string? RowIdSource,
    long SchemaVersion);

internal sealed record MissingTable(long SchemaVersion);

internal readonly union LiveTableOutcome(LiveTable, MissingTable, SqlRefusal);

internal sealed record GuardPlan(string Name, string Table, IReadOnlyList<string>? Readable, CompiledCondition Condition, LiveTable? Live, List<string>? Keys)
{
    public List<string>? Keys { get; set; } = Keys;
}

internal sealed record RuleSource(int Group, string? Table, string Alias);

internal sealed record EndUserStatement(
    string Sql,
    SqlToken[] Tokens,
    string[] Rendered,
    int Start,
    int Main,
    FrozenSet<string> Ctes,
    bool HasReturning,
    bool HasWildcard);

internal sealed class RulesEnforcer(RulesStore store, TimeProvider time)
{
    private const int MaxCacheEntries = 4096;

    private const int MaxCachedSqlLength = 4096;

    private const string GuardPrefix = "_tb_rule_";

    private static readonly FrozenSet<string> ClauseEnds = FrozenSet.Create(AsciiIgnoreCaseComparer.Instance,
        "WHERE", "GROUP", "ORDER", "LIMIT", "HAVING", "WINDOW", "UNION", "EXCEPT", "INTERSECT", "ON", "USING", "RETURNING", "SET", "VALUES",
        "JOIN", "LEFT", "RIGHT", "FULL", "INNER", "CROSS", "NATURAL", "OUTER", "OFFSET");

    private static readonly FrozenSet<string> JoinWords = FrozenSet.Create(AsciiIgnoreCaseComparer.Instance,
        "LEFT", "RIGHT", "FULL", "INNER", "CROSS", "NATURAL", "OUTER", "ON", "USING");

    private static readonly FrozenSet<string> TailClauses = FrozenSet.Create(AsciiIgnoreCaseComparer.Instance, "RETURNING", "ORDER", "LIMIT");

    private static readonly FrozenSet<string> RowIdNames = FrozenSet.Create(AsciiIgnoreCaseComparer.Instance, "rowid", "_rowid_", "oid");

    private static readonly FrozenSet<string> TableFunctions = FrozenSet.Create(AsciiIgnoreCaseComparer.Instance, "json_each", "json_tree");

    private static readonly FrozenSet<string> SqlKeywords = FrozenSet.Create(AsciiIgnoreCaseComparer.Instance,
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "TRUE", "FALSE", "AS", "BY", "ORDER", "ASC", "DESC",
        "LIMIT", "OFFSET", "GROUP", "HAVING", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "ON", "USING", "CASE", "WHEN",
        "THEN", "ELSE", "END", "CAST", "COLLATE", "LIKE", "GLOB", "BETWEEN", "DISTINCT", "ALL", "UNION", "INTERSECT", "EXCEPT",
        "EXISTS", "WITH", "RECURSIVE", "UPDATE", "SET", "DELETE", "INSERT", "INTO", "VALUES", "RETURNING", "ROWID", "_ROWID_", "OID");

    private static readonly string[] RowIdOrder = ["rowid", "_rowid_", "oid"];

    private static readonly FrozenDictionary<string, string> EmptyGuards = FrozenDictionary<string, string>.Empty;

    private static readonly FrozenDictionary<string, SqlTableScope> EmptyTables = FrozenDictionary<string, SqlTableScope>.Empty;

    private static readonly ConditionalWeakTable<IReadOnlyList<string>, FrozenSet<string>> AsciiSetCache = new();

    private static readonly ConditionalWeakTable<IReadOnlyList<string>, string> QuotedColumnsCache = new();

    private static readonly ConcurrentDictionary<string, EndUserStatement> ParsedCache = new(StringComparer.Ordinal);

    private static int _parsedCacheCount;

    private readonly ConcurrentDictionary<LiveTableKey, CachedLiveTable> _liveTables = new(LiveTableKeyComparer.Instance);

    private int _liveTableCount;

    private readonly ConcurrentDictionary<EnforcedTemplateKey, EnforcedTemplate> _templates = new();

    private int _templateCount;

    public ValueTask<EnforcementOutcome> ApplyAsync(
        Ulid databaseId, RulesAuth auth, IReadOnlyList<PlannedStatement> statements, CancellationToken cancellationToken, CatalogReader? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(statements);
        var session = catalog?.Target is SessionCatalog sessionCatalog ? sessionCatalog.Session : null;
        return ApplyCoreAsync(databaseId, auth, statements, session, catalog, cancellationToken);
    }

    public ValueTask<EnforcementOutcome> ApplyAsync(
        Ulid databaseId, RulesAuth auth, IReadOnlyList<PlannedStatement> statements, ISqlDatabaseSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(session);
        return ApplyCoreAsync(databaseId, auth, statements, session, catalog: null, cancellationToken);
    }

    private ValueTask<EnforcementOutcome> ApplyCoreAsync(
        Ulid databaseId,
        RulesAuth auth,
        IReadOnlyList<PlannedStatement> statements,
        ISqlDatabaseSession? session,
        CatalogReader? catalog,
        CancellationToken cancellationToken)
    {
        var cacheSession = session is not null && session.DatabaseId == databaseId ? session : null;
        var loaded = store.CurrentAsync(databaseId, cancellationToken);
        if (loaded.IsCompletedSuccessfully)
        {
            return loaded.Result switch
            {
                StoredRules current when cacheSession is not null && TryRenderAllCached(current.Version, cacheSession, auth, statements, out var fast) =>
                    ValueTask.FromResult<EnforcementOutcome>(fast),
                StoredRules current => RewriteAllAsync(current.Rules, current.Version, session, cacheSession, auth, statements, catalog, cancellationToken),
                ResourceNotFound => DenyMissingRulesAsync(session, auth, statements, catalog, cancellationToken),
                BackendUnavailable unavailable => ValueTask.FromResult<EnforcementOutcome>(SqlRefusal.From(unavailable)),
            };
        }

        return ApplySlowAsync(loaded, session, cacheSession, auth, statements, catalog, cancellationToken);
    }

    private async ValueTask<EnforcementOutcome> ApplySlowAsync(
        ValueTask<StoredRulesLookup> loadedTask,
        ISqlDatabaseSession? session,
        ISqlDatabaseSession? cacheSession,
        RulesAuth auth,
        IReadOnlyList<PlannedStatement> statements,
        CatalogReader? catalog,
        CancellationToken cancellationToken)
    {
        var loaded = await loadedTask;
        return loaded switch
        {
            StoredRules current when cacheSession is not null && TryRenderAllCached(current.Version, cacheSession, auth, statements, out var fast) => fast,
            StoredRules current => await RewriteAllAsync(current.Rules, current.Version, session, cacheSession, auth, statements, catalog, cancellationToken),
            ResourceNotFound => await DenyMissingRulesAsync(session, auth, statements, catalog, cancellationToken),
            BackendUnavailable unavailable => SqlRefusal.From(unavailable),
        };
    }

    private bool TryRenderAllCached(
        string rulesVersion,
        ISqlDatabaseSession session,
        RulesAuth auth,
        IReadOnlyList<PlannedStatement> statements,
        [NotNullWhen(true)] out EnforcedSql? enforced)
    {
        var count = statements.Count;
        if (count == 0)
        {
            enforced = new EnforcedSql([], rulesVersion);
            return true;
        }

        var databaseId = session.DatabaseId;
        var incarnation = session.SessionIncarnation;
        var epoch = session.SchemaEpoch;
        var first = statements[0];
        if (first.Statement.Sql.Length > MaxCachedSqlLength
            || !_templates.TryGetValue(new EnforcedTemplateKey(databaseId, incarnation, epoch, rulesVersion, first.Statement.Sql, first.Kind), out var firstTemplate))
        {
            enforced = null;
            return false;
        }

        if (count == 1)
        {
            enforced = new EnforcedSql([RenderTemplate(firstTemplate, auth, first.Statement)], rulesVersion);
            return true;
        }

        var rewritten = new SqlStatement[count];
        rewritten[0] = RenderTemplate(firstTemplate, auth, first.Statement);
        for (var i = 1; i < count; i++)
        {
            var planned = statements[i];
            if (planned.Statement.Sql.Length > MaxCachedSqlLength
                || !_templates.TryGetValue(new EnforcedTemplateKey(databaseId, incarnation, epoch, rulesVersion, planned.Statement.Sql, planned.Kind), out var template))
            {
                enforced = null;
                return false;
            }

            rewritten[i] = RenderTemplate(template, auth, planned.Statement);
        }

        enforced = new EnforcedSql(rewritten, rulesVersion);
        return true;
    }

    private async ValueTask<EnforcementOutcome> DenyMissingRulesAsync(
        ISqlDatabaseSession? session, RulesAuth auth, IReadOnlyList<PlannedStatement> statements, CatalogReader? catalog, CancellationToken cancellationToken)
    {
        var checkedEmpty = await RewriteAllAsync(new RulesSet(FrozenDictionary<string, RulesTable>.Empty), "", session, cacheSession: null, auth, statements, catalog, cancellationToken);
        return checkedEmpty switch
        {
            SqlRefusal refusal => refusal,
            EnforcedSql => SqlRefusal.RuleDenied("the database has no rules, so end users are denied", 0),
        };
    }

    private async ValueTask<EnforcementOutcome> RewriteAllAsync(
        RulesSet rules,
        string rulesVersion,
        ISqlDatabaseSession? session,
        ISqlDatabaseSession? cacheSession,
        RulesAuth auth,
        IReadOnlyList<PlannedStatement> statements,
        CatalogReader? catalog,
        CancellationToken cancellationToken)
    {
        var rewritten = new SqlStatement[statements.Count];
        for (var index = 0; index < statements.Count; index++)
        {
            var outcome = await RewriteWithCacheAsync(rules, rulesVersion, session, cacheSession, auth, statements[index], index, catalog, cancellationToken);
            var failure = outcome switch
            {
                SqlStatement statement => Assign(rewritten, index, statement),
                SqlRefusal refusal => refusal,
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        return new EnforcedSql(rewritten, rulesVersion);
    }

    private async ValueTask<StatementEnforcement> RewriteWithCacheAsync(
        RulesSet rules,
        string rulesVersion,
        ISqlDatabaseSession? session,
        ISqlDatabaseSession? cacheSession,
        RulesAuth auth,
        PlannedStatement planned,
        int index,
        CatalogReader? catalog,
        CancellationToken cancellationToken)
    {
        var canCache = cacheSession is not null && rulesVersion.Length > 0 && planned.Statement.Sql.Length <= MaxCachedSqlLength;
        ulong epoch = 0;
        long incarnation = 0;
        EnforcedTemplateKey key = default;
        if (canCache && cacheSession is not null)
        {
            epoch = cacheSession.SchemaEpoch;
            incarnation = cacheSession.SessionIncarnation;
            key = new EnforcedTemplateKey(cacheSession.DatabaseId, incarnation, epoch, rulesVersion, planned.Statement.Sql, planned.Kind);
            if (_templates.TryGetValue(key, out var cached))
            {
                return RenderTemplate(cached, auth, planned.Statement);
            }
        }

        var outcome = await BuildTemplateAsync(rules, planned, index, session, catalog, cancellationToken);
        return outcome switch
        {
            EnforcedTemplate built => RenderAndCache(built, canCache, cacheSession, key, epoch, incarnation, auth, planned.Statement),
            SqlRefusal refusal => refusal,
        };
    }

    private SqlStatement RenderAndCache(
        EnforcedTemplate built,
        bool canCache,
        ISqlDatabaseSession? cacheSession,
        EnforcedTemplateKey key,
        ulong epoch,
        long incarnation,
        RulesAuth auth,
        SqlStatement statement)
    {
        if (canCache && cacheSession is not null && cacheSession.SchemaEpoch == epoch && cacheSession.SessionIncarnation == incarnation)
        {
            StoreTemplate(key, built);
        }

        return RenderTemplate(built, auth, statement);
    }

    private void StoreTemplate(EnforcedTemplateKey key, EnforcedTemplate template)
    {
        if (!_templates.ContainsKey(key) && Volatile.Read(ref _templateCount) >= MaxCacheEntries)
        {
            _templates.Clear();
            Volatile.Write(ref _templateCount, 0);
        }

        if (_templates.TryAdd(key, template))
        {
            Interlocked.Increment(ref _templateCount);
        }
        else
        {
            _templates[key] = template;
        }
    }

    private SqlStatement RenderTemplate(EnforcedTemplate template, RulesAuth auth, SqlStatement statement)
    {
        if (template.Conditions.Length == 0)
        {
            return statement with { Sql = template.StaticChunks[0], Scope = template.Scope };
        }

        long? renderTime = template.UsesRequestTime ? time.GetUtcNow().ToUnixTimeMilliseconds() : null;
        Span<char> initial = stackalloc char[512];
        var builder = new CharBuffer(initial);
        try
        {
            builder.Append(template.StaticChunks[0]);
            var conditions = template.Conditions;
            for (var i = 0; i < conditions.Length; i++)
            {
                RulesCompiler.AppendCondition(ref builder, conditions[i], auth, renderTime);
                builder.Append(template.StaticChunks[i + 1]);
            }

            return statement with { Sql = builder.BuildString(), Scope = template.Scope };
        }
        finally
        {
            builder.Dispose();
        }
    }

    public async ValueTask<StatementEnforcement> RewriteAsync(
        RulesSet rules, RulesAuth auth, PlannedStatement planned, int index, CatalogReader? catalog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(planned);
        var session = catalog?.Target is SessionCatalog sessionCatalog ? sessionCatalog.Session : null;
        var outcome = await BuildTemplateAsync(rules, planned, index, session, catalog, cancellationToken);
        return outcome switch
        {
            EnforcedTemplate built => RenderTemplate(built, auth, planned.Statement),
            SqlRefusal refusal => refusal,
        };
    }

    private async ValueTask<TemplateBuildOutcome> BuildTemplateAsync(
        RulesSet rules, PlannedStatement planned, int index, ISqlDatabaseSession? session, CatalogReader? catalog, CancellationToken cancellationToken)
    {
        if (planned.Kind is StatementKind.Schema or StatementKind.Begin or StatementKind.Commit or StatementKind.Rollback)
        {
            return SqlRefusal.RuleDenied("end users cannot change the schema or control transactions", index);
        }

        return Parse(planned.Statement.Sql, index) switch
        {
            EndUserStatement statement => await EnforceTemplateAsync(rules, planned, statement, index, session, catalog, cancellationToken),
            SqlRefusal refusal => refusal,
        };
    }

    private async ValueTask<TemplateBuildOutcome> EnforceTemplateAsync(
        RulesSet rules, PlannedStatement planned, EndUserStatement statement, int index, ISqlDatabaseSession? session, CatalogReader? catalog, CancellationToken cancellationToken)
    {
        var main = statement.Tokens[statement.Main];
        if (main.Is("PRAGMA"))
        {
            return SqlRefusal.RuleDenied("end users cannot run PRAGMA statements", index);
        }

        return planned.Kind switch
        {
            StatementKind.Read when main.Is("SELECT") || main.Is("VALUES") =>
                await BuildReadsTemplateAsync(rules, statement, index, skipTarget: -1, new SchemaVersions(), session, catalog, cancellationToken) switch
                {
                    ReadTemplate read => AssembleReadTemplate(statement, read),
                    SqlRefusal refusal => refusal,
                },
            StatementKind.Read => SqlRefusal.RuleDenied("end users run SELECT, UPDATE and DELETE statements", index),
            StatementKind.Write => await BuildWriteTemplateAsync(rules, statement, index, session, catalog, cancellationToken),
            StatementKind.Schema or StatementKind.Begin or StatementKind.Commit or StatementKind.Rollback =>
                SqlRefusal.RuleDenied("end users cannot change the schema or control transactions", index),
            _ => throw new UnreachableException(),
        };
    }

    private async ValueTask<TemplateBuildOutcome> BuildWriteTemplateAsync(
        RulesSet rules, EndUserStatement statement, int index, ISqlDatabaseSession? session, CatalogReader? catalog, CancellationToken cancellationToken)
    {
        var tokens = statement.Tokens;
        var verb = tokens[statement.Main];
        if (!verb.Is("UPDATE") && !verb.Is("DELETE"))
        {
            return SqlRefusal.RuleDenied("end users insert rows through the rows API, which checks the create rule", index);
        }

        if (statement.HasReturning)
        {
            return SqlRefusal.RuleDenied("end users cannot use RETURNING; read the changed rows with SELECT", index);
        }

        var deleting = verb.Is("DELETE");
        var targetAt = statement.Main + 1;
        if (targetAt < tokens.Length && tokens[targetAt].Is("OR"))
        {
            return SqlRefusal.RuleDenied("end users cannot use OR conflict clauses such as OR REPLACE or OR IGNORE", index);
        }

        if (deleting && targetAt < tokens.Length && tokens[targetAt].Is("FROM"))
        {
            targetAt++;
        }

        if (targetAt >= tokens.Length || !tokens[targetAt].IsName)
        {
            return SqlRefusal.RuleDenied("the statement's target table could not be read", index);
        }

        if (targetAt + 1 < tokens.Length && tokens[targetAt + 1].Kind == SqlTokenKind.Dot)
        {
            if (!IsMain(tokens[targetAt]) || targetAt + 2 >= tokens.Length || !tokens[targetAt + 2].IsName)
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

        var written = AssignedPositions(tokens, targetAt);
        var writableSet = rule.Writable is { } writable ? CachedAsciiSet(writable) : null;
        if (!deleting && writableSet is not null && written is not null && FirstBlockedColumn(tokens, written, writableSet) is { } blocked)
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
        var readableColumns = effective is null ? null : CachedAsciiSet(effective);
        var targetAlias = targetAt + 2 < tokens.Length && tokens[targetAt + 1].Is("AS") && (tokens[targetAt + 2].IsName || tokens[targetAt + 2].Kind == SqlTokenKind.String)
            ? tokens[targetAt + 2].Value
            : table;
        var versions = new SchemaVersions();
        LiveTable? targetLive = null;
        if ((session is not null || catalog is not null)
            && Track(await LiveTableAsync(session, catalog, table, index, cancellationToken), ref versions, out targetLive) is { } targetFailure)
        {
            return targetFailure;
        }

        if (Unreadable(tokens, targetAt, targetAlias, readableColumns, targetLive, written) is { } hidden)
        {
            return SqlRefusal.ColumnNotReadable(hidden, index);
        }

        var targetVisible = BuildVisibleScope(readableColumns, compiled, HasRowIdKeys(tokens, targetAt, targetAlias, targetLive, written));
        var target = new SqlTargetScope(table, targetVisible, readableColumns, writableSet);
        var rewrittenReads = await BuildReadsTemplateAsync(rules, statement, index, skipTarget: targetAt, versions, session, catalog, cancellationToken);
        return rewrittenReads switch
        {
            ReadTemplate read => AssembleWriteTemplate(statement, read, compiled, target),
            SqlRefusal refusal => refusal,
        };
    }

    private async ValueTask<ReadTemplateOutcome> BuildReadsTemplateAsync(
        RulesSet rules,
        EndUserStatement statement,
        int index,
        int skipTarget,
        SchemaVersions versions,
        ISqlDatabaseSession? session,
        CatalogReader? catalog,
        CancellationToken cancellationToken)
    {
        var tokens = statement.Tokens;
        var pieces = (string[])statement.Rendered.Clone();
        Dictionary<string, GuardPlan>? plans = null;
        List<GuardPlan>? ordered = null;
        List<RuleSource>? sources = statement.HasWildcard ? [] : null;
        var depth = 0;
        Stack<(int Depth, int Group)>? fromDepths = null;
        var expectTable = false;
        var anyKeys = false;
        for (var at = 0; at < tokens.Length; at++)
        {
            var token = tokens[at];
            switch (token.Kind)
            {
                case SqlTokenKind.Open:
                    depth++;
                    if (expectTable && !(at + 1 < tokens.Length && (tokens[at + 1].Is("SELECT") || tokens[at + 1].Is("VALUES") || tokens[at + 1].Is("WITH"))))
                    {
                        fromDepths ??= new Stack<(int Depth, int Group)>();
                        fromDepths.Push((depth, Group(fromDepths)));
                        continue;
                    }

                    if (expectTable)
                    {
                        sources?.Add(new RuleSource(Group(fromDepths), null, ""));
                    }

                    expectTable = false;
                    continue;
                case SqlTokenKind.Close:
                    depth--;
                    while (fromDepths is { Count: > 0 } && fromDepths.Peek().Depth > depth)
                    {
                        fromDepths.Pop();
                    }

                    expectTable = false;
                    continue;
            }

            if (token.Is("IN") && at + 1 < tokens.Length && (tokens[at + 1].Kind == SqlTokenKind.String
                || (tokens[at + 1].IsName && !(at + 2 < tokens.Length && tokens[at + 2].Kind == SqlTokenKind.Open))))
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
                        fromDepths ??= new Stack<(int Depth, int Group)>();
                        fromDepths.Push((depth, at));
                    }

                    expectTable = true;
                }
                else if (token.Kind == SqlTokenKind.Comma && fromDepths is { Count: > 0 } && fromDepths.Peek().Depth == depth)
                {
                    expectTable = true;
                }
                else if (token.Kind == SqlTokenKind.Word && ClauseEnds.Contains(token.Value) && fromDepths is { Count: > 0 } && fromDepths.Peek().Depth == depth
                    && !JoinWords.Contains(token.Value))
                {
                    fromDepths.Pop();
                }

                continue;
            }

            expectTable = false;
            var start = at;
            var qualified = false;
            if (at + 1 < tokens.Length && tokens[at + 1].Kind == SqlTokenKind.Dot)
            {
                if (!IsMain(token) || at + 2 >= tokens.Length || !tokens[at + 2].IsName)
                {
                    return SqlRefusal.RuleDenied("end users read only tables of the main database", index);
                }

                at += 2;
                qualified = true;
            }

            if (at + 1 < tokens.Length && tokens[at + 1].Kind == SqlTokenKind.Open)
            {
                if (qualified || !TableFunctions.Contains(tokens[at].Value))
                {
                    return SqlRefusal.RuleDenied(string.Concat("end users cannot read ", tokens[at].Value), index, "read", tokens[at].Value);
                }

                sources?.Add(new RuleSource(Group(fromDepths), null, ""));
                continue;
            }

            var table = tokens[at].Value;
            var aliased = at + 1 < tokens.Length && (tokens[at + 1].Is("AS") || tokens[at + 1].Kind == SqlTokenKind.String
                || (tokens[at + 1].IsName && !(tokens[at + 1].Kind == SqlTokenKind.Word && ClauseEnds.Contains(tokens[at + 1].Value))));
            var aliasAt = !aliased ? -1 : tokens[at + 1].Is("AS") ? at + 2 : at + 1;
            var alias = aliasAt >= 0 && aliasAt < tokens.Length ? tokens[aliasAt].Value : table;
            var closeAt = aliasAt >= 0 ? aliasAt + 1 : at + 1;
            var openAt = start - 1;
            var outerClose = closeAt;
            while (openAt >= 0 && outerClose < tokens.Length && tokens[openAt].Kind == SqlTokenKind.Open && tokens[outerClose].Kind == SqlTokenKind.Close)
            {
                openAt--;
                outerClose++;
            }

            if (outerClose > closeAt)
            {
                var outerAt = outerClose < tokens.Length && tokens[outerClose].Is("AS") ? outerClose + 1 : outerClose;
                if (outerAt < tokens.Length && (tokens[outerAt].Kind == SqlTokenKind.String
                    || (tokens[outerAt].IsName && !(tokens[outerAt].Kind == SqlTokenKind.Word && ClauseEnds.Contains(tokens[outerAt].Value)))))
                {
                    alias = tokens[outerAt].Value;
                }
            }

            if (at == skipTarget || (!qualified && statement.Ctes.Contains(table)))
            {
                sources?.Add(new RuleSource(Group(fromDepths), null, alias));
                continue;
            }

            if (rules.Table(table) is not { Read: { } read } rule)
            {
                return SqlRefusal.RuleDenied(string.Concat("the rules do not allow reading ", table), index, "read", table);
            }

            LiveTable? live = null;
            GuardPlan? existingPlan = null;
            if (plans is not null && plans.TryGetValue(table, out existingPlan))
            {
                live = existingPlan.Live;
            }
            else if ((session is not null || catalog is not null)
                && Track(await LiveTableAsync(session, catalog, table, index, cancellationToken), ref versions, out live) is { } liveFailure)
            {
                return liveFailure;
            }

            if (live is not null && rule.Columns is { Count: > 0 } listed && HasMissingColumn(listed, live.DeclaredSet))
            {
                return SqlRefusal.RulesInvalid(string.Concat("the read columns of ", table, " reference a missing column; deploy the rules again"), index);
            }

            var compiled = RulesCompiler.Compile(read, live?.Declared, RulesOperation.Read) switch
            {
                CompiledCondition condition => condition,
                InvalidInput => null,
            };
            if (compiled is null)
            {
                return SqlRefusal.RulesInvalid(string.Concat("the read rule of ", table, " no longer compiles; deploy the rules again"), index);
            }

            var readable = rule.Columns is { Count: > 0 } listedColumns ? listedColumns : null;
            var readableSet = readable is null ? null : CachedAsciiSet(readable);
            if (Unreadable(tokens, at, alias, readableSet, live, null) is { } hiddenColumn)
            {
                return SqlRefusal.ColumnNotReadable(hiddenColumn, index);
            }

            GuardPlan plan;
            if (existingPlan is not null)
            {
                plan = existingPlan;
            }
            else
            {
                plans ??= new Dictionary<string, GuardPlan>(AsciiIgnoreCaseComparer.Instance);
                ordered ??= [];
                plan = new GuardPlan(string.Concat(GuardPrefix, ordered.Count.ToString(CultureInfo.InvariantCulture)), table, readable, compiled, live, null);
                plans[table] = plan;
                ordered.Add(plan);
            }

            if (CollectRowIdKeys(tokens, at, alias, live, null, plan.Keys) is { } keys)
            {
                plan.Keys = keys;
                anyKeys = true;
            }

            sources?.Add(new RuleSource(Group(fromDepths), table, alias));
            pieces[start] = aliased ? RulesCompiler.Quote(plan.Name) : string.Concat(RulesCompiler.Quote(plan.Name), " AS ", RulesCompiler.Quote(table));
            for (var consumed = start + 1; consumed <= at; consumed++)
            {
                pieces[consumed] = "";
            }
        }

        if (versions.Conflict)
        {
            return new SqlRefusal("serialization_conflict", "the schema changed while the rules were checked; retry the statement", "40001", index,
                StatusCodes.Status409Conflict);
        }

        if (ordered is null || ordered.Count == 0)
        {
            return new ReadTemplate(pieces, [], [], EmptyGuards, EmptyTables, versions.Version);
        }

        Dictionary<string, IReadOnlyList<string>?>? guardColumns = anyKeys ? new Dictionary<string, IReadOnlyList<string>?>(ordered.Count, AsciiIgnoreCaseComparer.Instance) : null;
        HashSet<string>? keyed = anyKeys ? new HashSet<string>(AsciiIgnoreCaseComparer.Instance) : null;
        var guards = new Dictionary<string, string>(ordered.Count, AsciiIgnoreCaseComparer.Instance);
        var scoped = new Dictionary<string, SqlTableScope>(ordered.Count, AsciiIgnoreCaseComparer.Instance);
        var guardHeaders = new string[ordered.Count];
        var guardConditions = new CompiledCondition[ordered.Count];
        var headerBuilder = new StringBuilder();
        for (var i = 0; i < ordered.Count; i++)
        {
            var plan = ordered[i];
            headerBuilder.Clear();

            var hasPlanKeys = plan.Keys is { Count: > 0 };
            if (hasPlanKeys && plan.Keys is { } planKeys)
            {
                planKeys.Sort(static (left, right) => Array.IndexOf(RowIdOrder, left).CompareTo(Array.IndexOf(RowIdOrder, right)));
                keyed?.Add(plan.Table);
            }

            if (guardColumns is not null)
            {
                guardColumns[plan.Table] = plan.Readable ?? plan.Live?.Names;
            }

            headerBuilder.Append(RulesCompiler.Quote(plan.Name));
            headerBuilder.Append(" AS (SELECT ");
            if (hasPlanKeys && plan.Keys is { } keys)
            {
                for (var k = 0; k < keys.Count; k++)
                {
                    if (k > 0)
                    {
                        headerBuilder.Append(", ");
                    }

                    headerBuilder.Append(plan.Live?.RowIdSource ?? keys[k]);
                    headerBuilder.Append(" AS ");
                    headerBuilder.Append(RulesCompiler.Quote(keys[k]));
                }

                headerBuilder.Append(", ");
            }

            headerBuilder.Append(plan.Readable is null ? "*" : CachedQuotedColumns(plan.Readable));
            headerBuilder.Append(" FROM main.");
            headerBuilder.Append(RulesCompiler.Quote(plan.Table));
            headerBuilder.Append(" WHERE ");
            guardHeaders[i] = headerBuilder.ToString();
            guardConditions[i] = plan.Condition;

            guards[plan.Name] = plan.Table;
            var readableSet = plan.Readable is null ? null : CachedAsciiSet(plan.Readable);
            scoped[plan.Table] = new SqlTableScope(BuildVisibleScope(readableSet, plan.Condition, hasPlanKeys));
        }

        if (keyed is { Count: > 0 } && guardColumns is not null && sources is not null
            && ExpandWildcards(tokens, pieces, sources, keyed, guardColumns) is { } wildcard)
        {
            return SqlRefusal.RuleDenied(wildcard, index);
        }

        return new ReadTemplate(
            pieces,
            guardHeaders,
            guardConditions,
            guards.ToFrozenDictionary(AsciiIgnoreCaseComparer.Instance),
            scoped.ToFrozenDictionary(AsciiIgnoreCaseComparer.Instance),
            versions.Version);
    }

    private static EnforcedTemplate AssembleReadTemplate(EndUserStatement statement, ReadTemplate read)
    {
        var scope = new SqlRuleScope(read.Tables, statement.Ctes, read.Guards, null, read.SchemaVersion);
        var guardCount = read.GuardConditions.Length;
        if (guardCount == 0)
        {
            return new EnforcedTemplate([Join(read.Pieces)], [], scope, UsesRequestTime: false);
        }

        var tokens = statement.Tokens;
        var pieces = read.Pieces;
        var hasWith = tokens[statement.Start].Is("WITH");
        var insertAt = hasWith ? statement.Start + (tokens[statement.Start + 1].Is("RECURSIVE") ? 2 : 1) : statement.Start;
        var chunks = new string[guardCount + 1];
        chunks[0] = BuildGuardPrefix(pieces, insertAt, hasWith, read.GuardHeaders[0]);
        for (var i = 1; i < guardCount; i++)
        {
            chunks[i] = string.Concat("), ", read.GuardHeaders[i]);
        }

        var tail = JoinRange(pieces, insertAt + 1, pieces.Length);
        chunks[guardCount] = tail.Length == 0
            ? string.Concat(hasWith ? "), " : ") ", pieces[insertAt])
            : string.Concat(hasWith ? "), " : ") ", pieces[insertAt], " ", tail);
        return new EnforcedTemplate(chunks, read.GuardConditions, scope, AnyUsesRequestTime(read.GuardConditions));
    }

    private static EnforcedTemplate AssembleWriteTemplate(EndUserStatement statement, ReadTemplate read, CompiledCondition writeCondition, SqlTargetScope target)
    {
        var scope = new SqlRuleScope(read.Tables, statement.Ctes, read.Guards, target, read.SchemaVersion);
        var tokens = statement.Tokens;
        var pieces = read.Pieces;
        FindWhereAndTail(tokens, statement.Main, out var whereAt, out var tailAt);
        var end = tailAt >= 0 ? tailAt : pieces.Length;
        var tail = JoinRange(pieces, end, pieces.Length);
        var finalChunk = tail.Length == 0 ? "" : string.Concat(" ", tail);
        var guardCount = read.GuardConditions.Length;
        if (guardCount == 0)
        {
            var sb = new StringBuilder(64);
            AppendWritePredicatePrefix(sb, pieces, 0, whereAt, end);
            return new EnforcedTemplate([sb.ToString(), finalChunk], [writeCondition], scope, writeCondition.UsesRequestTime);
        }

        var hasWith = tokens[statement.Start].Is("WITH");
        var insertAt = hasWith ? statement.Start + (tokens[statement.Start + 1].Is("RECURSIVE") ? 2 : 1) : statement.Start;
        var chunks = new string[guardCount + 2];
        var conditions = new CompiledCondition[guardCount + 1];
        chunks[0] = BuildGuardPrefix(pieces, insertAt, hasWith, read.GuardHeaders[0]);
        conditions[0] = read.GuardConditions[0];
        for (var i = 1; i < guardCount; i++)
        {
            chunks[i] = string.Concat("), ", read.GuardHeaders[i]);
            conditions[i] = read.GuardConditions[i];
        }

        var mid = new StringBuilder(64);
        mid.Append(hasWith ? "), " : ") ");
        mid.Append(pieces[insertAt]);
        AppendWritePredicatePrefix(mid, pieces, insertAt + 1, whereAt, end);
        chunks[guardCount] = mid.ToString();
        conditions[guardCount] = writeCondition;
        chunks[guardCount + 1] = finalChunk;
        return new EnforcedTemplate(chunks, conditions, scope, writeCondition.UsesRequestTime || AnyUsesRequestTime(read.GuardConditions));
    }

    private static string BuildGuardPrefix(string[] pieces, int insertAt, bool hasWith, string firstGuardHeader)
    {
        var before = JoinRange(pieces, 0, insertAt);
        if (hasWith)
        {
            return before.Length == 0 ? firstGuardHeader : string.Concat(before, " ", firstGuardHeader);
        }

        return before.Length == 0 ? string.Concat("WITH ", firstGuardHeader) : string.Concat(before, " WITH ", firstGuardHeader);
    }

    private static void FindWhereAndTail(SqlToken[] tokens, int main, out int whereAt, out int tailAt)
    {
        var depth = 0;
        whereAt = -1;
        tailAt = -1;
        for (var at = main; at < tokens.Length; at++)
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
    }

    private static void AppendWritePredicatePrefix(StringBuilder sb, string[] pieces, int start, int whereAt, int end)
    {
        if (whereAt < 0)
        {
            AppendPieces(sb, pieces, start, end);
            AppendPart(sb, "WHERE");
        }
        else
        {
            AppendPieces(sb, pieces, start, whereAt + 1);
            AppendPart(sb, "(");
            AppendPieces(sb, pieces, whereAt + 1, end);
            AppendPart(sb, ") AND");
        }

        sb.Append(' ');
    }

    private static bool AnyUsesRequestTime(CompiledCondition[] conditions)
    {
        for (var i = 0; i < conditions.Length; i++)
        {
            if (conditions[i].UsesRequestTime)
            {
                return true;
            }
        }

        return false;
    }

    private static FrozenSet<string>? BuildVisibleScope(FrozenSet<string>? readableSet, CompiledCondition condition, bool hasRowIdKey)
    {
        if (readableSet is null)
        {
            return null;
        }

        HashSet<string>? expanded = null;
        var parts = condition.Parts;
        for (var i = 0; i < parts.Count; i++)
        {
            var text = parts[i] switch
            {
                string literal => literal,
                ConditionParameter => null,
            };
            if (text is not null && text.StartsWith("row.\"", StringComparison.Ordinal) && text.EndsWith('"'))
            {
                var column = text[5..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
                if (!readableSet.Contains(column))
                {
                    expanded ??= new HashSet<string>(readableSet, AsciiIgnoreCaseComparer.Instance);
                    expanded.Add(column);
                }
            }
        }

        if (hasRowIdKey && !readableSet.Contains("rowid"))
        {
            expanded ??= new HashSet<string>(readableSet, AsciiIgnoreCaseComparer.Instance);
            expanded.Add("rowid");
        }

        return expanded is not null ? expanded.ToFrozenSet(AsciiIgnoreCaseComparer.Instance) : readableSet;
    }

    private static FrozenSet<string> CachedAsciiSet(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return FrozenSet<string>.Empty;
        }

        return AsciiSetCache.GetValue(items, static list => list.ToFrozenSet(AsciiIgnoreCaseComparer.Instance));
    }

    private static string CachedQuotedColumns(IReadOnlyList<string> columns) =>
        QuotedColumnsCache.GetValue(columns, static list =>
        {
            var sb = new StringBuilder();
            for (var i = 0; i < list.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(RulesCompiler.Quote(list[i]));
            }

            return sb.ToString();
        });

    private static bool HasMissingColumn(IReadOnlyList<string> listed, FrozenSet<string> declaredSet)
    {
        for (var i = 0; i < listed.Count; i++)
        {
            if (!declaredSet.Contains(listed[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static EndUserStatementOutcome Parse(string sql, int index)
    {
        if (sql.Length <= MaxCachedSqlLength && ParsedCache.TryGetValue(sql, out var cached))
        {
            return cached;
        }

        if (sql.Contains('\0', StringComparison.Ordinal))
        {
            return SqlRefusal.RuleDenied("end-user SQL cannot contain NUL characters", index);
        }

        if (!SqlText.TryTokenize(sql, out var tokens) || tokens.Count == 0)
        {
            return SqlRefusal.RuleDenied("the statement could not be read", index);
        }

        var cursor = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
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

        var count = tokens[^1].Kind == SqlTokenKind.Semicolon ? tokens.Count - 1 : tokens.Count;
        if (count == 0)
        {
            return SqlRefusal.RuleDenied("end users send exactly one statement per entry", index);
        }

        var hasReturning = false;
        var hasWildcard = false;
        var meaningful = new SqlToken[count];
        for (var i = 0; i < count; i++)
        {
            var token = tokens[i];
            if (token.Kind == SqlTokenKind.Semicolon)
            {
                return SqlRefusal.RuleDenied("end users send exactly one statement per entry", index);
            }

            if (token.Is("RETURNING"))
            {
                hasReturning = true;
            }
            else if (token is { Kind: SqlTokenKind.Symbol, Value: "*" })
            {
                hasWildcard = true;
            }

            meaningful[i] = token;
        }

        var shaped = Shape(sql, meaningful, hasReturning, hasWildcard, index);
        if (sql.Length <= MaxCachedSqlLength)
        {
            _ = shaped switch
            {
                EndUserStatement valid => StoreParsed(sql, valid),
                SqlRefusal => false,
            };
        }

        return shaped;
    }

    private static bool StoreParsed(string sql, EndUserStatement parsed)
    {
        if (Volatile.Read(ref _parsedCacheCount) >= MaxCacheEntries)
        {
            ParsedCache.Clear();
            Volatile.Write(ref _parsedCacheCount, 0);
        }

        if (ParsedCache.TryAdd(sql, parsed))
        {
            Interlocked.Increment(ref _parsedCacheCount);
        }

        return true;
    }

    private static EndUserStatementOutcome Shape(string sql, SqlToken[] tokens, bool hasReturning, bool hasWildcard, int index)
    {
        var start = tokens[0].Is("EXPLAIN") ? (tokens.Length > 2 && tokens[1].Is("QUERY") && tokens[2].Is("PLAN") ? 3 : 1) : 0;
        var at = start;
        HashSet<string>? ctes = null;
        if (at < tokens.Length && tokens[at].Is("WITH"))
        {
            ctes = new HashSet<string>(AsciiIgnoreCaseComparer.Instance);
            at++;
            if (at < tokens.Length && tokens[at].Is("RECURSIVE"))
            {
                at++;
            }

            while (true)
            {
                if (at >= tokens.Length || !tokens[at].IsName)
                {
                    return UnreadableWith(index);
                }

                if (tokens[at].Value.StartsWith("_tb_", StringComparison.OrdinalIgnoreCase))
                {
                    return SqlRefusal.RuleDenied("common table expression names starting with _tb_ are reserved", index);
                }

                ctes.Add(tokens[at].Value);
                at++;
                if (at < tokens.Length && tokens[at].Kind == SqlTokenKind.Open)
                {
                    at = AfterGroup(tokens, at);
                }

                if (at < 0 || at >= tokens.Length || !tokens[at].Is("AS"))
                {
                    return UnreadableWith(index);
                }

                at++;
                if (at < tokens.Length && tokens[at].Is("NOT"))
                {
                    at++;
                }

                if (at < tokens.Length && tokens[at].Is("MATERIALIZED"))
                {
                    at++;
                }

                if (at >= tokens.Length || tokens[at].Kind != SqlTokenKind.Open)
                {
                    return UnreadableWith(index);
                }

                at = AfterGroup(tokens, at);
                if (at < 0)
                {
                    return UnreadableWith(index);
                }

                if (at < tokens.Length && tokens[at].Kind == SqlTokenKind.Comma)
                {
                    at++;
                    continue;
                }

                break;
            }
        }

        if (at >= tokens.Length || tokens[at].Kind != SqlTokenKind.Word)
        {
            return SqlRefusal.RuleDenied("the statement could not be read", index);
        }

        for (var position = 0; position < tokens.Length; position++)
        {
            if (position != start && tokens[position].Is("WITH"))
            {
                return SqlRefusal.RuleDenied("end users declare common table expressions only at the start of the statement", index);
            }
        }

        var rendered = new string[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            rendered[i] = Render(sql, tokens[i]);
        }

        return new EndUserStatement(sql, tokens, rendered, start, at,
            ctes is null ? FrozenSet<string>.Empty : ctes.ToFrozenSet(AsciiIgnoreCaseComparer.Instance), hasReturning, hasWildcard);
    }

    private static SqlRefusal UnreadableWith(int index) => SqlRefusal.RuleDenied("the WITH clause could not be read", index);

    private static int AfterGroup(SqlToken[] tokens, int open)
    {
        var depth = 0;
        for (var at = open; at < tokens.Length; at++)
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

    private static string Join(string[] pieces) => JoinRange(pieces, 0, pieces.Length);

    private static string JoinRange(string[] pieces, int start, int end)
    {
        var total = 0;
        var nonEmpty = 0;
        for (var i = start; i < end; i++)
        {
            var length = pieces[i].Length;
            if (length > 0)
            {
                total += length;
                nonEmpty++;
            }
        }

        if (nonEmpty == 0)
        {
            return "";
        }

        total += nonEmpty - 1;
        return string.Create(total, (pieces, start, end), static (span, state) =>
        {
            var (items, from, to) = state;
            var offset = 0;
            for (var i = from; i < to; i++)
            {
                var item = items[i];
                if (item.Length == 0)
                {
                    continue;
                }

                if (offset > 0)
                {
                    span[offset++] = ' ';
                }

                item.CopyTo(span[offset..]);
                offset += item.Length;
            }
        });
    }

    public static CatalogReader Catalog(ISqlDatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new SessionCatalog(session).ReadAsync;
    }

    public static CatalogReader Catalog(ISqlTransaction transaction) => transaction.ReadCatalogAsync;

    private async ValueTask<LiveTableOutcome> LiveTableAsync(
        ISqlDatabaseSession? session, CatalogReader? catalog, string table, int index, CancellationToken cancellationToken)
    {
        if (session is not null)
        {
            var epoch = session.SchemaEpoch;
            var incarnation = session.SessionIncarnation;
            var key = new LiveTableKey(session.DatabaseId, incarnation, table, epoch);
            if (_liveTables.TryGetValue(key, out var cached))
            {
                return cached.Live is { } live ? live : new MissingTable(cached.SchemaVersion);
            }

            var fetched = await QueryLiveTableAsync(session, table, index, cancellationToken);
            if (session.SchemaEpoch == epoch && session.SessionIncarnation == incarnation)
            {
                _ = fetched switch
                {
                    LiveTable live => StoreLiveTable(key, new CachedLiveTable(live, live.SchemaVersion)),
                    MissingTable missing => StoreLiveTable(key, new CachedLiveTable(null, missing.SchemaVersion)),
                    SqlRefusal => false,
                };
            }

            return fetched;
        }

        if (catalog is null)
        {
            return SqlRefusal.RuleDenied("the table schema could not be read", index);
        }

        return await QueryLiveTableAsync(catalog, table, index, cancellationToken);
    }

    private bool StoreLiveTable(LiveTableKey key, CachedLiveTable entry)
    {
        if (!_liveTables.ContainsKey(key) && Volatile.Read(ref _liveTableCount) >= MaxCacheEntries)
        {
            _liveTables.Clear();
            Volatile.Write(ref _liveTableCount, 0);
        }

        if (_liveTables.TryAdd(key, entry))
        {
            Interlocked.Increment(ref _liveTableCount);
        }
        else
        {
            _liveTables[key] = entry;
        }

        return true;
    }

    private static SqlStatement[] BuildLiveTableQueries(string table)
    {
        var name = RulesCompiler.Literal(table);
        return
        [
            new SqlStatement(string.Concat("SELECT name, type, pk, hidden FROM pragma_table_xinfo(", name, ")"), [], SqlBuild.NoNamed),
            new SqlStatement(string.Concat("SELECT type, wr FROM pragma_table_list(", name, ") WHERE schema = 'main'"), [], SqlBuild.NoNamed),
            new SqlStatement(string.Concat("SELECT count(*) FROM pragma_index_list(", name, ") WHERE origin = 'pk'"), [], SqlBuild.NoNamed),
            new SqlStatement("PRAGMA schema_version", [], SqlBuild.NoNamed),
        ];
    }

    private static async ValueTask<LiveTableOutcome> QueryLiveTableAsync(ISqlDatabaseSession session, string table, int index, CancellationToken cancellationToken)
    {
        var outcome = await session.ExecuteReadAsync(
            new SqlStatementRequest(session.DatabaseId, BuildLiveTableQueries(table), SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty),
            cancellationToken);
        return outcome switch
        {
            SqlReadResult read => Describe(read.ResultSets, index),
            SqlError error => SqlRefusal.From(error),
            StorageBusy busy => SqlRefusal.From(busy),
            StorageTimeout timeout => SqlRefusal.From(timeout),
        };
    }

    private static async ValueTask<LiveTableOutcome> QueryLiveTableAsync(CatalogReader catalog, string table, int index, CancellationToken cancellationToken)
    {
        var outcome = await catalog(BuildLiveTableQueries(table), cancellationToken);
        return outcome switch
        {
            SqlReadResult read => Describe(read.ResultSets, index),
            SqlError error => SqlRefusal.From(error),
            StorageBusy busy => SqlRefusal.From(busy),
            StorageTimeout timeout => SqlRefusal.From(timeout),
        };
    }

    private static LiveTableOutcome Describe(IReadOnlyList<SqlResultSet> results, int index)
    {
        if (results is not [{ Rows: var columns }, { Rows: var kinds }, { Rows: [var keyIndexes] }, { Rows: [[var version]] }] || !version.TryGetIntegral(out var schemaVersion))
        {
            return SqlRefusal.RuleDenied("the table schema could not be read", index);
        }

        if (columns.Count == 0 || kinds is not [var kind])
        {
            return new MissingTable(schemaVersion);
        }

        var hasRowId = SqlBuild.Format(kind[0]) == "table" && kind[1].TryGetIntegral(out var withoutRowId) && withoutRowId == 0;
        var declared = new string[columns.Count];
        IReadOnlyList<SqlValue>? singleKey = null;
        var keyCount = 0;
        var visibleCount = 0;
        for (var i = 0; i < columns.Count; i++)
        {
            var row = columns[i];
            declared[i] = SqlBuild.Format(row[0]);
            if (row[2].TryGetIntegral(out var key) && key > 0)
            {
                keyCount++;
                singleKey = row;
            }

            if (!(row[3].TryGetIntegral(out var hidden) && hidden == 1))
            {
                visibleCount++;
            }
        }

        var declaredSet = declared.ToFrozenSet(AsciiIgnoreCaseComparer.Instance);
        var alias = hasRowId && keyCount == 1 && singleKey is not null && string.Equals(SqlBuild.Format(singleKey[1]), "INTEGER", StringComparison.OrdinalIgnoreCase)
            && keyIndexes[0].TryGetIntegral(out var pkIndexes) && pkIndexes == 0
            ? SqlBuild.Format(singleKey[0])
            : null;
        string? source = null;
        if (hasRowId)
        {
            for (var i = 0; i < RowIdOrder.Length; i++)
            {
                if (!declaredSet.Contains(RowIdOrder[i]))
                {
                    source = RowIdOrder[i];
                    break;
                }
            }
        }

        string[] names;
        if (visibleCount == columns.Count)
        {
            names = declared;
        }
        else
        {
            names = new string[visibleCount];
            var next = 0;
            for (var i = 0; i < columns.Count; i++)
            {
                if (!(columns[i][3].TryGetIntegral(out var hidden) && hidden == 1))
                {
                    names[next++] = declared[i];
                }
            }
        }

        return new LiveTable(names, declared, declaredSet, alias, source, schemaVersion);
    }

    private static SqlRefusal? Track(LiveTableOutcome outcome, ref SchemaVersions versions, out LiveTable? live)
    {
        (live, var version, var failure) = outcome switch
        {
            LiveTable table => (table, (long?)table.SchemaVersion, (SqlRefusal?)null),
            MissingTable missing => ((LiveTable?)null, (long?)missing.SchemaVersion, (SqlRefusal?)null),
            SqlRefusal refusal => ((LiveTable?)null, (long?)null, refusal),
        };
        if (version is { } schemaVersion)
        {
            versions.Record(schemaVersion);
        }

        return failure;
    }

    private static int Group(Stack<(int Depth, int Group)>? fromDepths) => fromDepths is { Count: > 0 } ? fromDepths.Peek().Group : -1;

    private static bool IsRowIdReference(SqlToken[] tokens, int at) =>
        tokens[at].IsName && RowIdNames.Contains(tokens[at].Value) && !(at > 0 && tokens[at - 1].Is("AS"))
        && !(at + 1 < tokens.Length && tokens[at + 1].Kind is SqlTokenKind.Open or SqlTokenKind.Dot);

    private static bool RefersTo(SqlToken[] tokens, int at, string alias) =>
        !(at >= 2 && tokens[at - 1].Kind == SqlTokenKind.Dot && tokens[at - 2].IsName) || AsciiIgnoreCaseComparer.Instance.Equals(tokens[at - 2].Value, alias);

    private static bool HasRowIdKeys(SqlToken[] tokens, int tableAt, string alias, LiveTable? live, HashSet<int>? written)
    {
        for (var at = 0; at < tokens.Length; at++)
        {
            if (at == tableAt || (written is not null && written.Contains(at)) || !IsRowIdReference(tokens, at) || !RefersTo(tokens, at, alias))
            {
                continue;
            }

            var name = MatchRowIdOrder(tokens[at].Value);
            if (live?.DeclaredSet.Contains(name) != true)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string>? CollectRowIdKeys(SqlToken[] tokens, int tableAt, string alias, LiveTable? live, HashSet<int>? written, List<string>? existing)
    {
        var keys = existing;
        for (var at = 0; at < tokens.Length; at++)
        {
            if (at == tableAt || (written is not null && written.Contains(at)) || !IsRowIdReference(tokens, at) || !RefersTo(tokens, at, alias))
            {
                continue;
            }

            var name = MatchRowIdOrder(tokens[at].Value);
            if (live?.DeclaredSet.Contains(name) != true)
            {
                keys ??= [];
                if (!ContainsAsciiIgnoreCase(keys, name))
                {
                    keys.Add(name);
                }
            }
        }

        return keys;
    }

    private static string MatchRowIdOrder(string value)
    {
        for (var i = 0; i < RowIdOrder.Length; i++)
        {
            if (AsciiIgnoreCaseComparer.Instance.Equals(RowIdOrder[i], value))
            {
                return RowIdOrder[i];
            }
        }

        return RowIdOrder[0];
    }

    private static bool ContainsAsciiIgnoreCase(List<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (AsciiIgnoreCaseComparer.Instance.Equals(list[i], value))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Unreadable(
        SqlToken[] tokens, int tableAt, string alias, FrozenSet<string>? visibleColumns, LiveTable? live, HashSet<int>? written)
    {
        for (var at = 0; at < tokens.Length; at++)
        {
            if (at == tableAt || (written is not null && written.Contains(at)) || !IsRowIdReference(tokens, at) || !RefersTo(tokens, at, alias))
            {
                continue;
            }

            var name = tokens[at].Value;
            if (live is not null && live.DeclaredSet.Contains(name))
            {
                if (visibleColumns is not null && !visibleColumns.Contains(name))
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
                || (live.RowIdAlias is { } keyAlias && !visibleColumns.Contains(keyAlias))
                || (live.DeclaredSet.Contains("rowid") && !visibleColumns.Contains("rowid"))))
            {
                return name;
            }
        }

        return visibleColumns is null || live is null ? null : FindHiddenColumn(tokens, tableAt, live.Declared, visibleColumns, written);
    }

    private static string? ExpandWildcards(
        SqlToken[] tokens, string[] pieces, List<RuleSource> sources, HashSet<string> keyed, Dictionary<string, IReadOnlyList<string>?> guardColumns)
    {
        for (var at = 0; at < tokens.Length; at++)
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
            if (qualified)
            {
                for (var s = 0; s < sources.Count; s++)
                {
                    var source = sources[s];
                    if (source.Group == group && source.Table is { } table && AsciiIgnoreCaseComparer.Instance.Equals(source.Alias, tokens[at - 2].Value))
                    {
                        if (keyed.Contains(table) && guardColumns.TryGetValue(table, out var namedColumns))
                        {
                            if (namedColumns is null)
                            {
                                return string.Concat("list the columns of ", table, " instead of * when reading rowid");
                            }

                            pieces[at - 2] = Columns(source.Alias, namedColumns);
                            pieces[at - 1] = "";
                            pieces[at] = "";
                        }

                        break;
                    }
                }

                continue;
            }

            var hasKeyedMember = false;
            var hasNullTable = false;
            for (var s = 0; s < sources.Count; s++)
            {
                var source = sources[s];
                if (source.Group != group)
                {
                    continue;
                }

                if (source.Table is null)
                {
                    hasNullTable = true;
                }
                else if (keyed.Contains(source.Table))
                {
                    hasKeyedMember = true;
                }
            }

            if (!hasKeyedMember)
            {
                continue;
            }

            if (hasNullTable || ContainsNaturalOrUsing(tokens))
            {
                return "list the columns instead of * when reading rowid";
            }

            var expanded = new StringBuilder();
            var expandedCount = 0;
            for (var s = 0; s < sources.Count; s++)
            {
                var member = sources[s];
                if (member.Group != group)
                {
                    continue;
                }

                IReadOnlyList<string>? memberColumns = member.Table is not null && guardColumns.TryGetValue(member.Table, out var knownColumns) ? knownColumns : null;
                if (memberColumns is null)
                {
                    return "list the columns instead of * when reading rowid";
                }

                if (expandedCount > 0)
                {
                    expanded.Append(", ");
                }

                AppendColumns(expanded, member.Alias, memberColumns);
                expandedCount++;
            }

            pieces[at] = expanded.ToString();
        }

        return null;
    }

    private static bool ContainsNaturalOrUsing(SqlToken[] tokens)
    {
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Is("NATURAL") || tokens[i].Is("USING"))
            {
                return true;
            }
        }

        return false;
    }

    private static string Columns(string alias, IReadOnlyList<string> columns)
    {
        var sb = new StringBuilder();
        AppendColumns(sb, alias, columns);
        return sb.ToString();
    }

    private static void AppendColumns(StringBuilder sb, string alias, IReadOnlyList<string> columns)
    {
        var quotedAlias = RulesCompiler.Quote(alias);
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(quotedAlias);
            sb.Append('.');
            sb.Append(RulesCompiler.Quote(columns[i]));
        }
    }

    private static int FromOf(SqlToken[] tokens, int at)
    {
        var depth = 0;
        for (var next = at + 1; next < tokens.Length; next++)
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

    private static HashSet<int>? AssignedPositions(SqlToken[] tokens, int targetAt)
    {
        var setAt = -1;
        for (var at = targetAt; at < tokens.Length; at++)
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

        HashSet<int>? positions = null;
        var depth = 0;
        var listing = false;
        for (var at = setAt + 1; at < tokens.Length; at++)
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
                positions ??= [];
                positions.Add(at);
            }
            else if (depth == 0 && (token.Is("WHERE") || token.Is("FROM") || token.Is("RETURNING")))
            {
                break;
            }
            else if (depth == 0 && (at == setAt + 1 || tokens[at - 1].Kind == SqlTokenKind.Comma) && (token.IsName || token.Kind == SqlTokenKind.String)
                && at + 1 < tokens.Length && tokens[at + 1] is { Kind: SqlTokenKind.Symbol, Value: "=" })
            {
                positions ??= [];
                positions.Add(at);
            }
        }

        return positions;
    }

    private static string? FirstBlockedColumn(SqlToken[] tokens, HashSet<int> written, FrozenSet<string> writableSet)
    {
        var firstPos = int.MaxValue;
        string? blocked = null;
        foreach (var position in written)
        {
            var name = tokens[position].Value;
            if (!writableSet.Contains(name) && position < firstPos)
            {
                firstPos = position;
                blocked = name;
            }
        }

        return blocked;
    }

    private static string? FindHiddenColumn(SqlToken[] tokens, int tableAt, IReadOnlyList<string> liveNames, FrozenSet<string> visibleColumns, HashSet<int>? written)
    {
        Dictionary<string, string>? hidden = null;
        for (var i = 0; i < liveNames.Count; i++)
        {
            var name = liveNames[i];
            if (!visibleColumns.Contains(name))
            {
                hidden ??= new Dictionary<string, string>(AsciiIgnoreCaseComparer.Instance);
                hidden.TryAdd(name, name);
            }
        }

        if (hidden is null)
        {
            return null;
        }

        for (var at = 0; at < tokens.Length; at++)
        {
            var token = tokens[at];
            if (!token.IsName || at == tableAt || (written is not null && written.Contains(at)))
            {
                continue;
            }

            if (at > 0 && tokens[at - 1].Is("AS"))
            {
                continue;
            }

            if (at + 1 < tokens.Length && tokens[at + 1].Kind == SqlTokenKind.Open)
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

    private static void AppendPieces(StringBuilder sb, string[] pieces, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var piece = pieces[i];
            if (piece.Length > 0)
            {
                AppendPart(sb, piece);
            }
        }
    }

    private static void AppendPart(StringBuilder sb, string part)
    {
        if (sb.Length > 0)
        {
            sb.Append(' ');
        }

        sb.Append(part);
    }

    private static SqlRefusal? Assign(SqlStatement[] statements, int index, SqlStatement statement)
    {
        statements[index] = statement;
        return null;
    }

    private struct SchemaVersions
    {
        public long? Version;
        public bool Conflict;

        public void Record(long version)
        {
            if (Version is { } first)
            {
                if (first != version)
                {
                    Conflict = true;
                }
            }
            else
            {
                Version = version;
            }
        }
    }

    private sealed class SessionCatalog(ISqlDatabaseSession session)
    {
        public ISqlDatabaseSession Session => session;

        public ValueTask<SqlReadOutcome> ReadAsync(IReadOnlyList<SqlStatement> statements, CancellationToken cancellationToken) =>
            session.ExecuteReadAsync(new SqlStatementRequest(session.DatabaseId, statements, SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty), cancellationToken);
    }

    private readonly record struct EnforcedTemplateKey(
        Ulid DatabaseId,
        long SessionIncarnation,
        ulong SchemaEpoch,
        string RulesVersion,
        string Sql,
        StatementKind Kind);

    private readonly record struct LiveTableKey(Ulid DatabaseId, long SessionIncarnation, string Table, ulong SchemaEpoch);

    private readonly record struct CachedLiveTable(LiveTable? Live, long SchemaVersion);

    private sealed class LiveTableKeyComparer : IEqualityComparer<LiveTableKey>
    {
        public static LiveTableKeyComparer Instance { get; } = new();

        public bool Equals(LiveTableKey x, LiveTableKey y) =>
            x.DatabaseId == y.DatabaseId
            && x.SessionIncarnation == y.SessionIncarnation
            && x.SchemaEpoch == y.SchemaEpoch
            && AsciiIgnoreCaseComparer.Instance.Equals(x.Table, y.Table);

        public int GetHashCode(LiveTableKey obj) =>
            HashCode.Combine(obj.DatabaseId, obj.SessionIncarnation, obj.SchemaEpoch, AsciiIgnoreCaseComparer.Instance.GetHashCode(obj.Table));
    }
}
