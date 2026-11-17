using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Features.Shapes;
using TerraBase.Primitives;

namespace TerraBase.Features.Rules;

internal sealed record RulesTestAuth(
    string Uid,
    IReadOnlyDictionary<string, JsonElement>? Claims = null,
    [property: JsonPropertyName("org_id")] string? OrgId = null,
    [property: JsonPropertyName("org_role")] string? OrgRole = null,
    [property: JsonPropertyName("is_anonymous")] bool IsAnonymous = false);

internal sealed record RulesTestRequest(
    string Operation,
    string Table,
    RulesTestAuth Auth,
    IReadOnlyDictionary<string, JsonElement>? Row = null,
    [property: JsonPropertyName("new")] IReadOnlyDictionary<string, JsonElement>? New = null,
    long? Time = null,
    RulesSet? Rules = null);

internal sealed record RulesTestValue(string Rule, bool Value);

internal sealed record RulesTestBody(bool Allowed, string DecidedBy, string? Clause, IReadOnlyList<RulesTestValue> Results,
    IReadOnlyList<string> Columns, IReadOnlyList<RulesProblem> Problems);

internal interface IRulesEvaluation : IRulesSchema
{
    object? Evaluate(RulesTableSchema table, CompiledCondition condition, IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, object?> parameters);
}

internal static class RulesSimulator
{
    internal static RulesTestBody Test(RulesTestRequest request, RulesSet rules, IRulesEvaluation engine)
    {
        var operation = request.Operation switch
        {
            "read" => RulesOperation.Read,
            "create" => RulesOperation.Create,
            "update" => RulesOperation.Update,
            "delete" => RulesOperation.Delete,
            _ => throw new UnreachableException(),
        };
        rules.Tables.TryGetValue(request.Table, out var rule);
        var checkedRules = request.Rules is not null ? rules : new RulesSet(rule is null
            ? new Dictionary<string, RulesTable>()
            : new Dictionary<string, RulesTable> { [request.Table] = Required(rule, operation) });
        var problems = RulesChecks.Problems(checkedRules, engine);
        var schema = engine.RulesTable(request.Table);
        var columns = rule?.Columns ?? schema.Columns;
        if (problems.Count > 0)
        {
            return new RulesTestBody(false, request.Operation, null, [], columns, problems);
        }

        List<RulesTestValue> results = [];
        var row = Values(request.Row);
        var values = request.Operation == "update" ? new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase) : Values(request.New);
        if (request.Operation == "update")
        {
            foreach (var (key, value) in Values(request.New))
            {
                values[key] = value;
            }
        }

        if (operation is RulesOperation.Update or RulesOperation.Delete && !Check("read", rule?.Read, RulesOperation.Read))
        {
            return new RulesTestBody(false, "read", rule?.Read, results, columns, []);
        }

        var set = operation == RulesOperation.Create ? rule?.Set?.Create : rule?.Set?.Update;
        if (operation is RulesOperation.Create or RulesOperation.Update)
        {
            foreach (var column in request.New?.Keys ?? [])
            {
                if (rule?.Writable?.Contains(column, StringComparer.OrdinalIgnoreCase) != true
                    || set?.Keys.Contains(column, StringComparer.OrdinalIgnoreCase) == true)
                {
                    results.Add(new RulesTestValue("writable", false));
                    return new RulesTestBody(false, "writable", column, results, columns, []);
                }
            }

            var ulid = EntityIds.New().ToString();
            foreach (var (column, value) in set ?? new Dictionary<string, string>())
            {
                values[column] = value == "request.ulid" ? ulid
                    : Evaluate((CompiledCondition)ShapeFilters.Where(value, new RuleScope(schema.Columns, RulesOperation.Create)), values);
            }
        }

        var clause = operation switch
        {
            RulesOperation.Read => rule?.Read,
            RulesOperation.Create => rule?.Create,
            RulesOperation.Update => rule?.Update,
            RulesOperation.Delete => rule?.Delete,
            RulesOperation.Validate => throw new UnreachableException(),
            _ => throw new UnreachableException(),
        };
        var allowed = Check(request.Operation, clause, operation);
        if (allowed && operation is RulesOperation.Create or RulesOperation.Update)
        {
            for (var index = 0; index < (rule?.Validate?.Count ?? 0); index++)
            {
                var validation = rule!.Validate![index];
                var field = $"validate[{index}]";
                if (!Check(field, validation, RulesOperation.Validate))
                {
                    return new RulesTestBody(false, field, validation, results, columns, []);
                }
            }
        }

        return new RulesTestBody(allowed, request.Operation, clause, results, columns, []);

        bool Check(string field, string? condition, RulesOperation kind)
        {
            var passed = condition is not null && Evaluate((CompiledCondition)RulesChecks.Compile(schema, condition, kind),
                kind is RulesOperation.Read or RulesOperation.Delete ? row : values) is long and not 0;
            results.Add(new RulesTestValue(field, passed));
            return passed;
        }

        object? Evaluate(CompiledCondition condition, IReadOnlyDictionary<string, object?> projection)
        {
            var parameters = condition.Parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Source switch
            {
                ConditionSource.Uid => request.Auth.Uid,
                ConditionSource.OrgId => request.Auth.OrgId,
                ConditionSource.OrgRole => request.Auth.OrgRole,
                ConditionSource.IsAnonymous => (object)(request.Auth.IsAnonymous ? 1L : 0L),
                ConditionSource.RequestTime => request.Time,
                ConditionSource.Row => row.GetValueOrDefault(parameter.Key!),
                ConditionSource.Claim => Claim(parameter.Key!, list: false),
                ConditionSource.ClaimList => Claim(parameter.Key!, list: true),
                ConditionSource.Subject => throw new UnreachableException(),
                _ => throw new UnreachableException(),
            });
            return engine.Evaluate(schema, condition, projection, parameters);
        }

        object? Claim(string name, bool list) => request.Auth.Claims?.TryGetValue(name, out var claim) == true
            ? list ? claim.ValueKind == JsonValueKind.Array ? claim.GetRawText() : null : Scalar(claim)
            : null;
    }

    private static RulesTable Required(RulesTable rule, RulesOperation operation) => operation switch
    {
        RulesOperation.Read => new RulesTable(Read: rule.Read, Columns: rule.Columns),
        RulesOperation.Create => new RulesTable(Create: rule.Create, Writable: rule.Writable, Validate: rule.Validate,
            Set: new RulesValues(Create: rule.Set?.Create)),
        RulesOperation.Update => new RulesTable(Read: rule.Read, Update: rule.Update, Columns: rule.Columns, Writable: rule.Writable,
            Validate: rule.Validate, Set: new RulesValues(Update: rule.Set?.Update)),
        RulesOperation.Delete => new RulesTable(Read: rule.Read, Delete: rule.Delete, Columns: rule.Columns),
        RulesOperation.Validate => throw new UnreachableException(),
        _ => throw new UnreachableException(),
    };

    private static Dictionary<string, object?> Values(IReadOnlyDictionary<string, JsonElement>? values) =>
        values?.ToDictionary(pair => pair.Key, pair => Scalar(pair.Value), StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);

    private static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var whole) ? (object)whole : value.GetDouble(),
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        _ => null,
    };
}
