using System.Globalization;
using TerraBase.Engine;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;

namespace TerraBase.Features.Rules;

public sealed record DeployedRules(StoredRules Rules);

public readonly union RulesDeployResponse(DeployedRules, InvalidRules, StaleRules, PreconditionFailed, Validation, Unauthorized, Forbidden, NotFound, Unavailable);

internal sealed class RulesService(DatabaseAccess access, RulesStore store)
{
    public const int MaxTables = 256;

    public async ValueTask<Result<ResolvedDatabase, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> ResolveAsync(
        Caller caller, string? project, string? database, string scope, CancellationToken cancellationToken) =>
        await access.ManagedAsync(caller, project, database, scope, cancellationToken);

    public async ValueTask<RulesDeployResponse> DeployAsync(
        Caller caller, string? project, string? database, RulesSet rules, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        if (ifMatch is null && !ifNoneMatch)
        {
            return new PreconditionFailed("send If-Match: \"<current version>\", or If-None-Match: * when the database has no rules", "precondition_required");
        }

        return await ResolveAsync(caller, project, database, Scopes.DatabasesWrite, cancellationToken) switch
        {
            ResolvedDatabase resolved => await access.OpenAsync(resolved.Database, cancellationToken) switch
            {
                ISqlDatabaseSession session => await ProblemsAsync(session, rules, cancellationToken) switch
                {
                    { Count: > 0 } problems => new InvalidRules(problems),
                    _ => await store.DeployAsync(resolved.Database.Id, rules, ifMatch, CallerContext.Actor(caller), cancellationToken) switch
                    {
                        StoredRules stored => new DeployedRules(stored),
                        StaleRules stale => stale,
                        Unavailable unavailable => unavailable,
                    },
                },
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
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

            Check(problems, table, "read", rule.Read, columns, RulesOperation.Read);
            Check(problems, table, "create", rule.Create, columns, RulesOperation.Create);
            Check(problems, table, "update", rule.Update, columns, RulesOperation.Update);
            Check(problems, table, "delete", rule.Delete, columns, RulesOperation.Delete);
            for (var index = 0; index < (rule.Validate?.Count ?? 0); index++)
            {
                Check(problems, table, string.Concat("validate[", index.ToString(CultureInfo.InvariantCulture), "]"), rule.Validate?[index], columns,
                    RulesOperation.Validate);
            }

            foreach (var column in (rule.Columns ?? []).Concat(rule.Writable ?? []).Where(column => !columns.Contains(column, StringComparer.OrdinalIgnoreCase)))
            {
                problems.Add(new RulesProblem(table, "columns", "unknown_column", string.Concat("the table has no column ", column)));
            }
        }

        return problems;
    }

    public static async ValueTask<IReadOnlyList<string>> ColumnsAsync(ISqlDatabaseSession session, string table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var request = new SqlStatementRequest(session.DatabaseId, [new SqlStatement(string.Concat("PRAGMA table_info(", RulesCompiler.Quote(table), ")"), [], SqlBuild.NoNamed)],
            SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty);
        return await session.ExecuteReadAsync(request, cancellationToken) switch
        {
            SqlReadResult { ResultSets: [.., var last] } => [.. last.Rows.Select(static row => SqlBuild.Format(row[1]))],
            SqlReadResult or SqlError or StorageBusy or RequestTimeout => [],
        };
    }

    private static void Check(List<RulesProblem> problems, string table, string field, string? condition, IReadOnlyList<string> columns, RulesOperation operation)
    {
        if (condition is null)
        {
            return;
        }

        if (RulesCompiler.Compile(condition, columns, operation) switch { CompiledCondition => null, Validation refused => refused } is { } failure)
        {
            problems.Add(new RulesProblem(table, field, failure.Code, failure.Message));
        }
    }
}
