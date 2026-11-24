using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rules;

internal readonly union TestRulesResponse(RulesTestBody, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal sealed class TestRulesEndpoint(RulesService rules, RulesStore store, DatabaseAccess access, TimeProvider time)
    : Endpoint<RulesTestInput, TestRulesResponse>
{
    public override void Configure()
    {
        Post(RulesRoute.Rules + ":test");
        Name("testDatabaseRules");
        Summary("Evaluates the rules for a simulated end user");
        Description("{\"operation\": read|create|update|delete, \"table\", \"auth\": {\"uid\", \"claims\", \"org_id\", \"org_role\", \"is_anonymous\"}, \"row\"?, \"new\"?, "
            + "\"time\"? (request.time in Unix milliseconds), \"rules\"? (a draft {\"tables\"} instead of the deployed rules)}: evaluates read (before update and delete), "
            + "writable and set, the operation's rule and validate in order without touching data. The answer names allowed, decidedBy, the deciding clause, every "
            + "evaluated rule's result, the readable columns and problems that keep the rules from compiling.");
        Tags("Rules");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<TestRulesResponse> HandleAsync(
        RulesTestInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RulesOperation? parsed = request.Operation switch
        {
            "read" => RulesOperation.Read,
            "create" => RulesOperation.Create,
            "update" => RulesOperation.Update,
            "delete" => RulesOperation.Delete,
            _ => null,
        };
        if (parsed is not { } operation || request.Operation is not { } name || request.Table is not { Length: > 0 } table
            || request.Auth is not { Uid: { Length: > 0 } uid } auth)
        {
            return new InvalidInput("table, operation (read, create, update or delete) and auth.uid are required", "invalid_request");
        }

        var simulation = new Simulation(table, operation, name, Auth(uid, auth), request);
        var outcome = await rules.ResolveAsync(HttpContext.Caller, request.Project, request.Database, Scopes.DatabasesRead, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved => await access.OpenAsync(resolved.Database, cancellationToken) switch
            {
                ISqlDatabaseSession session => request.Rules is { } draft
                    ? await SimulateAsync(session, draft, draft: true, simulation, cancellationToken)
                    : await store.CurrentAsync(resolved.Database.Id, fresh: true, cancellationToken) switch
                    {
                        StoredRules current => await SimulateAsync(session, current.Rules, draft: false, simulation, cancellationToken),
                        ResourceNotFound => await SimulateAsync(session, new RulesSet(new Dictionary<string, RulesTable>()), draft: false, simulation, cancellationToken),
                        BackendUnavailable unavailable => unavailable,
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

    private sealed record Simulation(string Table, RulesOperation Operation, string Name, RulesAuth Auth, RulesTestInput Request);

    private static RulesAuth Auth(string uid, RulesTestAuth auth)
    {
        var claims = auth.Claims is { ValueKind: JsonValueKind.Object } ? RulesAuth.Read(uid, Wrap(auth.Claims.Value), custom: false)?.Claims : null;
        return new RulesAuth(uid, claims ?? FrozenDictionary<string, RulesClaim>.Empty, auth.OrgId, auth.OrgRole, auth.IsAnonymous);
    }

    private static JsonElement Wrap(JsonElement claims)
    {
        using var document = JsonDocument.Parse(string.Concat("{\"claims\":", claims.GetRawText(), "}"));
        return document.RootElement.Clone();
    }

    private async ValueTask<TestRulesResponse> SimulateAsync(
        ISqlDatabaseSession session, RulesSet set, bool draft, Simulation simulation, CancellationToken cancellationToken)
    {
        var rule = set.Table(simulation.Table);
        var checkedRules = draft
            ? set
            : new RulesSet(rule is null
                ? new Dictionary<string, RulesTable>()
                : new Dictionary<string, RulesTable>(StringComparer.Ordinal) { [simulation.Table] = Required(rule, simulation.Operation) });
        var problems = await RulesService.ProblemsAsync(session, checkedRules, cancellationToken);
        var schema = await RulesService.ColumnsAsync(session, simulation.Table, cancellationToken);
        var columns = rule?.Columns ?? schema;
        if (problems.Count > 0)
        {
            return new RulesTestBody(false, simulation.Name, null, [], columns, problems);
        }

        var request = simulation.Request;
        var row = Values(request.Row);
        var values = simulation.Operation == RulesOperation.Update ? new Dictionary<string, SqlValue>(row, StringComparer.OrdinalIgnoreCase) : Values(request.New);
        if (simulation.Operation == RulesOperation.Update)
        {
            foreach (var (column, value) in Values(request.New))
            {
                values[column] = value;
            }
        }

        var evaluator = new Evaluator(session, schema, simulation.Auth, request.Time, row, values);
        List<RulesTestValue> results = [];
        if (simulation.Operation is RulesOperation.Update or RulesOperation.Delete
            && !await CheckAsync(evaluator, results, "read", rule?.Read, RulesOperation.Read, cancellationToken))
        {
            return new RulesTestBody(false, "read", rule?.Read, results, columns, []);
        }

        if (simulation.Operation is RulesOperation.Create or RulesOperation.Update)
        {
            var assigned = simulation.Operation == RulesOperation.Create ? rule?.Set?.Create : rule?.Set?.Update;
            foreach (var column in request.New?.Keys ?? [])
            {
                if (rule?.Writable?.Contains(column, StringComparer.OrdinalIgnoreCase) != true || assigned?.Keys.Contains(column, StringComparer.OrdinalIgnoreCase) == true)
                {
                    results.Add(new RulesTestValue("writable", false));
                    return new RulesTestBody(false, "writable", column, results, columns, []);
                }
            }

            var ulid = Ulid.NewUlid(time.GetUtcNow());
            foreach (var (column, expression) in assigned ?? new Dictionary<string, string>())
            {
                values[column] = RulesCompiler.SetValue(expression, simulation.Auth, request.Time, ulid);
            }
        }

        var clause = simulation.Operation switch
        {
            RulesOperation.Read => rule?.Read,
            RulesOperation.Create => rule?.Create,
            RulesOperation.Update => rule?.Update,
            RulesOperation.Delete => rule?.Delete,
            _ => throw new UnreachableException(),
        };
        var allowed = await CheckAsync(evaluator, results, simulation.Name, clause, simulation.Operation, cancellationToken);
        if (allowed && simulation.Operation is RulesOperation.Create or RulesOperation.Update)
        {
            for (var index = 0; index < (rule?.Validate?.Count ?? 0); index++)
            {
                var validation = rule?.Validate?[index];
                var field = string.Concat("validate[", index.ToString(CultureInfo.InvariantCulture), "]");
                if (!await CheckAsync(evaluator, results, field, validation, RulesOperation.Validate, cancellationToken))
                {
                    return new RulesTestBody(false, field, validation, results, columns, []);
                }
            }
        }

        return new RulesTestBody(allowed, simulation.Name, clause, results, columns, []);
    }

    private static async ValueTask<bool> CheckAsync(
        Evaluator evaluator, List<RulesTestValue> results, string field, string? condition, RulesOperation operation, CancellationToken cancellationToken)
    {
        var passed = condition is not null && await evaluator.EvaluateAsync(condition, operation, cancellationToken);
        results.Add(new RulesTestValue(field, passed));
        return passed;
    }

    private static RulesTable Required(RulesTable rule, RulesOperation operation) => operation switch
    {
        RulesOperation.Read => new RulesTable(Read: rule.Read, Columns: rule.Columns),
        RulesOperation.Create => new RulesTable(Create: rule.Create, Writable: rule.Writable, Validate: rule.Validate, Set: new RulesValues(Create: rule.Set?.Create)),
        RulesOperation.Update => new RulesTable(Read: rule.Read, Update: rule.Update, Columns: rule.Columns, Writable: rule.Writable, Validate: rule.Validate,
            Set: new RulesValues(Update: rule.Set?.Update)),
        RulesOperation.Delete => new RulesTable(Read: rule.Read, Delete: rule.Delete, Columns: rule.Columns),
        RulesOperation.Validate => throw new UnreachableException(),
        _ => throw new UnreachableException(),
    };

    private static Dictionary<string, SqlValue> Values(IReadOnlyDictionary<string, JsonElement>? values)
    {
        Dictionary<string, SqlValue> converted = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, element) in values ?? new Dictionary<string, JsonElement>())
        {
            converted[column] = SqlWire.Value(element) ?? SqlValue.Null;
        }

        return converted;
    }

    private sealed class Evaluator(
        ISqlDatabaseSession session, IReadOnlyList<string> columns, RulesAuth auth, long? requestTime,
        IReadOnlyDictionary<string, SqlValue> row, IReadOnlyDictionary<string, SqlValue> values)
    {
        public async ValueTask<bool> EvaluateAsync(string condition, RulesOperation operation, CancellationToken cancellationToken)
        {
            var compiled = RulesCompiler.Compile(condition, columns, operation) switch
            {
                CompiledCondition ready => ready,
                InvalidInput => null,
            };
            if (compiled is null || columns.Count == 0)
            {
                return false;
            }

            var projected = operation is RulesOperation.Read or RulesOperation.Delete ? row : values;
            List<SqlValue> args = [];
            var sql = new StringBuilder("SELECT ")
                .Append(RulesCompiler.Render(compiled, auth, requestTime, "new.", "row_values."))
                .Append(" FROM (SELECT ").Append(Projection(row, args))
                .Append(") AS row_values, (SELECT ").Append(Projection(projected, args)).Append(") AS new")
                .ToString();
            var request = new SqlStatementRequest(session.DatabaseId, [SqlBuild.Statement(sql, [.. args])], SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty);
            var outcome = await session.ExecuteReadAsync(request, cancellationToken);
            return outcome switch
            {
                SqlReadResult { ResultSets: [.., { Rows: [[var allowed, ..], ..] }] } => allowed.TryGetIntegral(out var value) && value != 0,
                SqlReadResult or SqlError or StorageBusy or StorageTimeout => false,
            };
        }

        private string Projection(IReadOnlyDictionary<string, SqlValue> source, List<SqlValue> args) =>
            string.Join(", ", columns.Select(column =>
            {
                args.Add(source.TryGetValue(column, out var value) ? value : SqlValue.Null);
                return string.Concat("?", args.Count.ToString(CultureInfo.InvariantCulture), " AS ", RulesCompiler.Quote(column));
            }));
    }
}
