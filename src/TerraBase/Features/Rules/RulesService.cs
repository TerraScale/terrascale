using System.Globalization;
using TerraBase.Engine;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Sql;
using TerraBase.Hosting;

namespace TerraBase.Features.Rules;

public sealed record DeployedRules(StoredRules Rules, IReadOnlyList<RulesProblem> Warnings);

public sealed record TableColumn(string Name, string? DefaultSql);

public sealed record RulesPreconditionRequired(string Message);

public readonly union RulesDeployResponse(DeployedRules, InvalidRules, StaleRules, RulesPreconditionRequired, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal sealed class RulesService(DatabaseAccess access, RulesStore store)
{
    public const int MaxTables = 256;

    public const int MaxColumns = 256;

    public const int MaxValidate = 32;

    public async ValueTask<DatabaseResolution> ResolveAsync(
        Caller caller, string? project, string? database, string scope, CancellationToken cancellationToken) =>
        await access.ManagedAsync(caller, project, database, scope, cancellationToken);

    public async ValueTask<RulesDeployResponse> DeployAsync(
        Caller caller, string? project, string? database, RulesSet rules, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        if (ifMatch is null && !ifNoneMatch)
        {
            return new RulesPreconditionRequired("send If-Match: \"<current version>\", or If-None-Match: * when the database has no rules");
        }

        var outcome = await ResolveAsync(caller, project, database, Scopes.DatabasesWrite, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved => await access.OpenAsync(resolved.Database, cancellationToken) switch
            {
                ISqlDatabaseSession session => await ProblemsAsync(session, rules, cancellationToken) switch
                {
                    { Count: > 0 } problems => new InvalidRules(problems),
                    _ => await store.DeployAsync(resolved.Database.Id, rules, ifMatch, CallerContext.Actor(caller), cancellationToken) switch
                    {
                        StoredRules stored => new DeployedRules(stored, Warnings(rules)),
                        StaleRules stale => stale,
                        BackendUnavailable unavailable => unavailable,
                    },
                },
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public static async ValueTask<IReadOnlyList<RulesProblem>> ProblemsAsync(ISqlDatabaseSession session, RulesSet rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(rules);
        List<RulesProblem> problems = [];
        if (rules.Tables.Count > MaxTables)
        {
            problems.Add(new RulesProblem("", "tables", "invalid_value", "a rules set holds at most 256 tables"));
            return problems;
        }

        foreach (var (table, rule) in rules.Tables)
        {
            var columns = await ColumnsAsync(session, table, cancellationToken);
            if (columns.Count == 0)
            {
                problems.Add(new RulesProblem(table, "", "unknown_table", string.Concat("the database has no table ", table)));
                continue;
            }

            Names(problems, table, "columns", rule.Columns, columns, 1);
            Names(problems, table, "writable", rule.Writable, columns, 0);
            if (rule.Validate is { Count: > MaxValidate })
            {
                problems.Add(new RulesProblem(table, "validate", "invalid_value", "validate holds at most 32 conditions"));
            }

            Check(problems, table, "read", rule.Read, columns, RulesOperation.Read);
            Check(problems, table, "create", rule.Create, columns, RulesOperation.Create);
            Check(problems, table, "update", rule.Update, columns, RulesOperation.Update);
            Check(problems, table, "delete", rule.Delete, columns, RulesOperation.Delete);
            for (var index = 0; index < (rule.Validate?.Count ?? 0); index++)
            {
                Check(problems, table, string.Concat("validate[", index.ToString(CultureInfo.InvariantCulture), "]"), rule.Validate?[index], columns,
                    RulesOperation.Validate);
            }

            Values(problems, table, "create", rule.Set?.Create, rule.Writable, columns);
            Values(problems, table, "update", rule.Set?.Update, rule.Writable, columns);
        }

        return problems;
    }

    public static IReadOnlyList<RulesProblem> Warnings(RulesSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        List<RulesProblem> warnings = [];
        foreach (var (table, rule) in rules.Tables)
        {
            foreach (var column in rule.Writable ?? [])
            {
                if (ComparesWithUid(rule.Read, "row", column) && !(ComparesWithUid(rule.Create, "new", column) && ComparesWithUid(rule.Update, "new", column)))
                {
                    warnings.Add(new RulesProblem(table, "writable", "unchecked_owner",
                        string.Concat(column, " is writable and read compares it with auth.uid, but create or update does not require new.", column, " = auth.uid")));
                }
            }
        }

        return warnings;
    }

    private static void Names(List<RulesProblem> problems, string table, string field, IReadOnlyList<string>? names, IReadOnlyList<string> columns, int least)
    {
        if (names is null)
        {
            return;
        }

        if (names.Count < least || names.Count > MaxColumns)
        {
            problems.Add(new RulesProblem(table, field, "invalid_value", string.Concat(field, " names ", least.ToString(CultureInfo.InvariantCulture), " to 256 columns")));
        }

        foreach (var column in names.Distinct(StringComparer.OrdinalIgnoreCase).Where(column => !columns.Contains(column, StringComparer.OrdinalIgnoreCase)))
        {
            problems.Add(new RulesProblem(table, field, "unknown_column", string.Concat(field, ": the table has no column ", column)));
        }

        if (names.Count != names.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            problems.Add(new RulesProblem(table, field, "invalid_value", string.Concat(field, " names a column twice")));
        }
    }

    private static void Values(
        List<RulesProblem> problems, string table, string operation, IReadOnlyDictionary<string, string>? values, IReadOnlyList<string>? writable, IReadOnlyList<string> columns)
    {
        foreach (var (column, value) in values ?? new Dictionary<string, string>())
        {
            var field = string.Concat("set.", operation, ".", column);
            if (!columns.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(new RulesProblem(table, field, "unknown_column", string.Concat(field, ": the table has no column ", column)));
            }

            if (writable?.Contains(column, StringComparer.OrdinalIgnoreCase) == true)
            {
                problems.Add(new RulesProblem(table, field, "column_conflict", string.Concat(column, " is in both writable and set")));
            }

            if (value == "request.ulid" && operation != "create")
            {
                problems.Add(new RulesProblem(table, field, "reference_not_allowed", "request.ulid is allowed only in set.create"));
            }
            else if (!RulesCompiler.IsSetValue(value))
            {
                problems.Add(new RulesProblem(table, field, "invalid_value",
                    "a set value is auth.uid, auth.claims.<name>, auth.org_id, auth.org_role, auth.is_anonymous, request.time, request.ulid or one literal"));
            }
        }
    }

    private static bool ComparesWithUid(string? condition, string head, string column)
    {
        if (condition is null || !SqlText.TryTokenize(condition, out var tokens))
        {
            return false;
        }

        for (var index = 0; index + 6 < tokens.Count; index++)
        {
            if (tokens[index + 3] is { Kind: SqlTokenKind.Symbol, Value: "=" }
                && ((Names(tokens, index, head, column) && Names(tokens, index + 4, "auth", "uid"))
                    || (Names(tokens, index, "auth", "uid") && Names(tokens, index + 4, head, column))))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Names(List<SqlToken> tokens, int at, string head, string name) =>
        at + 2 < tokens.Count && tokens[at].Is(head) && tokens[at + 1].Kind == SqlTokenKind.Dot && tokens[at + 2].IsName
        && string.Equals(tokens[at + 2].Value, name, StringComparison.OrdinalIgnoreCase);

    public static async ValueTask<IReadOnlyList<string>> ColumnsAsync(ISqlDatabaseSession session, string table, CancellationToken cancellationToken) =>
        [.. (await TableColumnsAsync(session, table, cancellationToken)).Select(static column => column.Name)];

    public static async ValueTask<IReadOnlyList<TableColumn>> TableColumnsAsync(ISqlDatabaseSession session, string table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var request = new SqlStatementRequest(session.DatabaseId, [new SqlStatement(string.Concat("PRAGMA table_info(", RulesCompiler.Quote(table), ")"), [], SqlBuild.NoNamed)],
            SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty);
        var outcome = await session.ExecuteReadAsync(request, cancellationToken);
        return outcome switch
        {
            SqlReadResult { ResultSets: [.., var last] } => [.. last.Rows.Select(static row => new TableColumn(SqlBuild.Format(row[1]),
                row.Count > 4 && row[4].TryGetText(out var defaultSql) ? defaultSql : null))],
            SqlReadResult or SqlError or StorageBusy or StorageTimeout => [],
        };
    }

    private static void Check(List<RulesProblem> problems, string table, string field, string? condition, IReadOnlyList<string> columns, RulesOperation operation)
    {
        if (condition is null)
        {
            return;
        }

        if (RulesCompiler.Compile(condition, columns, operation) switch { CompiledCondition => null, InvalidInput refused => refused } is { } failure)
        {
            problems.Add(new RulesProblem(table, field, failure.Code, failure.Message));
        }
    }
}
