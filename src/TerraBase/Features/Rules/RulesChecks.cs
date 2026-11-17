using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Shapes;

namespace TerraBase.Features.Rules;

internal static class RulesChecks
{
    internal const int MaxTables = 256;
    internal const int MaxColumns = 256;
    internal const int MaxValidate = 32;
    internal const int MaxBytes = 256 * 1024;

    private static readonly string[] AuthValues = ["auth.uid", "auth.org_id", "auth.org_role", "auth.is_anonymous", "request.time"];

    internal static object Compile(RulesTableSchema table, string condition, RulesOperation operation) =>
        ShapeFilters.Where(condition, new RuleScope(table.Columns, operation)) switch
        {
            CompiledCondition compiled => compiled with { Sql = $"(({compiled.Sql}) IS TRUE)" },
            var refused => refused,
        };

    internal static IReadOnlyList<RulesProblem> Problems(RulesDocument rules, IRulesSchema schema)
    {
        List<RulesProblem> problems = [];
        if (rules.Tables.Count > MaxTables || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(rules, RulesJsonContext.Default.RulesDocument)) > MaxBytes)
        {
            problems.Add(new RulesProblem("", "tables", "invalid_value", $"a rules set holds at most {MaxTables} tables and {MaxBytes / 1024} KiB"));
            return problems;
        }

        foreach (var (name, rule) in rules.Tables)
        {
            var table = ShapeTables.IsValidName(name) ? schema.RulesTable(name) : new RulesTableSchema(ShapeTableStatus.Missing, []);
            if (table.Status is ShapeTableStatus.Missing or ShapeTableStatus.Unsupported)
            {
                problems.Add(table.Status == ShapeTableStatus.Missing
                    ? new RulesProblem(name, "", "unknown_table", $"the database has no table {name}")
                    : new RulesProblem(name, "", "unsupported_table", "rules apply to ordinary rowid tables"));
                continue;
            }

            void Add(string field, string code, string message) => problems.Add(new RulesProblem(name, field, code, message));

            Names(rule.Columns, "columns", 1, MaxColumns);
            Names(rule.Writable, "writable", 0, MaxColumns);
            if (rule.Validate is { Count: > MaxValidate })
            {
                Add("validate", "invalid_value", $"validate holds at most {MaxValidate} conditions");
            }

            Condition("read", rule.Read, RulesOperation.Read);
            Condition("create", rule.Create, RulesOperation.Create);
            Condition("update", rule.Update, RulesOperation.Update);
            Condition("delete", rule.Delete, RulesOperation.Delete);
            for (var index = 0; index < (rule.Validate?.Count ?? 0); index++)
            {
                Condition($"validate[{index}]", rule.Validate![index], RulesOperation.Validate);
            }

            Values("create", rule.Set?.Create);
            Values("update", rule.Set?.Update);

            void Names(IReadOnlyList<string>? names, string field, int least, int most)
            {
                if (names is null)
                {
                    return;
                }

                if (names.Count < least || names.Count > most)
                {
                    Add(field, "invalid_value", $"{field} names {least} to {most} columns");
                }

                foreach (var column in names.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!table.Columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                    {
                        Add(field, "unknown_column", $"{field}: the table has no column {column}");
                    }
                }

                if (names.Count != names.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                {
                    Add(field, "invalid_value", $"{field} names a column twice");
                }
            }

            void Condition(string field, string? condition, RulesOperation operation)
            {
                switch (condition is null ? null : Compile(table, condition, operation))
                {
                    case null:
                        break;
                    case Validation refused:
                        Add(field, refused.Code, refused.Message);
                        break;
                    case CompiledCondition compiled when !schema.Runs(table, compiled):
                        Add(field, "unsafe_where", "the condition is not a valid expression on this table's columns");
                        break;
                }
            }

            void Values(string operation, IReadOnlyDictionary<string, string>? values)
            {
                foreach (var (column, value) in values ?? new Dictionary<string, string>())
                {
                    var field = $"set.{operation}.{column}";
                    if (!table.Columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                    {
                        Add(field, "unknown_column", $"{field}: the table has no column {column}");
                    }

                    if (rule.Writable?.Contains(column, StringComparer.OrdinalIgnoreCase) == true)
                    {
                        Add(field, "column_conflict", $"{column} is in both writable and set");
                    }

                    if (value == "request.ulid" && operation != "create")
                    {
                        Add(field, "reference_not_allowed", "request.ulid is allowed only in set.create");
                    }
                    else if (!IsValue(value))
                    {
                        Add(field, "invalid_value", "a set value is auth.uid, auth.claims.<name>, auth.org_id, auth.org_role, auth.is_anonymous, request.time, request.ulid or one literal");
                    }
                }
            }
        }

        return problems;
    }

    internal static IReadOnlyList<RulesProblem> Warnings(RulesDocument rules)
    {
        List<RulesProblem> warnings = [];
        foreach (var (name, rule) in rules.Tables)
        {
            foreach (var column in rule.Writable ?? [])
            {
                if (ComparesWithUid(rule.Read, "row", column)
                    && !(ComparesWithUid(rule.Create, "new", column) && ComparesWithUid(rule.Update, "new", column)))
                {
                    warnings.Add(new RulesProblem(name, "writable", "unchecked_owner",
                        $"{column} is writable and read compares it with auth.uid, but create or update does not require new.{column} = auth.uid"));
                }
            }
        }

        return warnings;
    }

    private static bool IsValue(string value)
    {
        if (AuthValues.Contains(value, StringComparer.Ordinal) || value == "request.ulid"
            || value.StartsWith("auth.claims.", StringComparison.Ordinal) && ShapeFilters.IsClaimName(value["auth.claims.".Length..]))
        {
            return true;
        }

        return SqlText.TryTokenize(value, out var tokens) && tokens switch
        {
            [{ Kind: SqlTokenKind.String or SqlTokenKind.Number or SqlTokenKind.Blob }] => true,
            [var word] => word.Is("NULL") || word.Is("TRUE") || word.Is("FALSE"),
            [{ Kind: SqlTokenKind.Symbol, Value: "-" }, { Kind: SqlTokenKind.Number }] => true,
            _ => false,
        };
    }

    private static bool ComparesWithUid(string? condition, string head, string column)
    {
        if (condition is null || !SqlText.TryTokenize(condition, out var tokens))
        {
            return false;
        }

        for (var index = 0; index + 6 < tokens.Count; index++)
        {
            if (tokens[index + 3] is { Kind: SqlTokenKind.Symbol, Value: "=" or "==" }
                && (Names(tokens, index, head, column) && Names(tokens, index + 4, "auth", "uid")
                    || Names(tokens, index, "auth", "uid") && Names(tokens, index + 4, head, column)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Names(List<SqlToken> tokens, int at, string head, string name) =>
        tokens[at].Is(head) && tokens[at + 1].Kind == SqlTokenKind.Dot && tokens[at + 2].IsName
        && string.Equals(tokens[at + 2].Value, name, StringComparison.OrdinalIgnoreCase);
}
