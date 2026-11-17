using System.Collections.Frozen;
using System.Text;
using TerraBase.Engine;
using TerraBase.Errors;

namespace TerraBase.Features.Shapes;

public sealed record ShapeFilter(string? Where, IReadOnlyList<string> Columns)
{
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = FrozenDictionary<string, object?>.Empty;

    public string RulesVersion { get; init; } = "";

    public string Binding { get; init; } = "";

    public string Key => (Where ?? "") + "\0" + string.Join('\u001f', Columns) + "\0" + RulesVersion + "\0" + Binding;
}

internal sealed record ShapeQuery(string? Where, string? Columns, ShapeViewer? Viewer = null)
{
    internal static readonly ShapeQuery None = new(null, null);

    internal bool IsEmpty => Where is null && Columns is null && Viewer is null;
}

internal enum RulesOperation
{
    Read,
    Create,
    Update,
    Delete,
    Validate,
}

internal enum ConditionSource
{
    Uid,
    OrgId,
    OrgRole,
    IsAnonymous,
    Claim,
    ClaimList,
    RequestTime,
    Row,
}

internal sealed record ConditionParameter(string Name, ConditionSource Source, string? Key = null);

internal sealed record CompiledCondition(string Sql, IReadOnlyList<ConditionParameter> Parameters);

internal abstract record ConditionScope(IReadOnlyList<string> Columns);

internal sealed record RequestScope(IReadOnlyList<string> Columns, bool Readable) : ConditionScope(Columns);

internal sealed record RuleScope(IReadOnlyList<string> Columns, RulesOperation Operation) : ConditionScope(Columns);

internal static class ShapeFilters
{
    internal const int MaxWhere = 4096;
    internal const int MaxColumns = 4096;

    private static readonly HashSet<string> Operators =
    [
        with(StringComparer.OrdinalIgnoreCase), "AND", "OR", "NOT", "IS", "NULL", "IN", "BETWEEN", "LIKE", "GLOB", "ESCAPE", "CASE", "WHEN",
        "THEN", "ELSE", "END", "COLLATE", "CAST", "AS", "TRUE", "FALSE",
    ];

    private static readonly HashSet<string> Types = [with(StringComparer.OrdinalIgnoreCase), "INTEGER", "INT", "TEXT", "REAL", "BLOB", "NUMERIC"];

    private static readonly HashSet<string> Collations = [with(StringComparer.OrdinalIgnoreCase), "BINARY", "NOCASE", "RTRIM"];

    private static readonly HashSet<string> Functions =
    [
        with(StringComparer.OrdinalIgnoreCase), "abs", "coalesce", "ifnull", "nullif", "iif", "length", "lower", "upper", "ltrim", "rtrim", "trim",
        "substr", "substring", "instr", "replace", "round", "typeof", "hex", "like", "glob",
    ];

    internal static Validation Refused(string message) => new(message, "unsafe_where");

    internal static object Parse(ShapeQuery query, IReadOnlyList<string> tableColumns)
    {
        IReadOnlyList<string> columns = tableColumns;
        if (query.Columns is { } list)
        {
            var chosen = Columns(list, tableColumns);
            if (chosen is not List<string> names)
            {
                return chosen;
            }

            columns = names;
        }

        string? condition = null;
        if (query.Where is { } where)
        {
            var checkedWhere = Where(where, new RequestScope(tableColumns, false));
            if (checkedWhere is not CompiledCondition text)
            {
                return checkedWhere;
            }

            condition = text.Sql;
        }

        return new ShapeFilter(condition, columns);
    }

    internal static Forbidden ColumnNotReadable(string column) =>
        new($"{column} is not a readable column of this table", "column_not_readable", new Dictionary<string, string> { ["column"] = column });

    internal static object Columns(string list, IReadOnlyList<string> tableColumns)
    {
        List<string> chosen = [];
        foreach (var part in list.Split(','))
        {
            var name = part.Trim();
            if (Column(tableColumns, name) is not { } column)
            {
                return new Validation($"columns names a column the table does not have: {name}", "unknown_column");
            }

            if (chosen.Contains(column, StringComparer.Ordinal))
            {
                return new Validation($"columns names {column} twice", "unknown_column");
            }

            chosen.Add(column);
        }

        return chosen;
    }

    internal static bool IsClaimName(string name) => name is { Length: >= 1 and <= 63 } && name[0] is >= 'a' and <= 'z'
        && name.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    internal static object Where(string where, ConditionScope scope)
    {
        if (where.Length > MaxWhere || !SqlText.TryTokenize(where, out var tokens) || tokens.Count == 0)
        {
            return Refused($"where must be a complete expression of at most {MaxWhere} characters");
        }

        var text = new StringBuilder();
        List<ConditionParameter> parameters = [];
        List<bool> lists = [];
        var readable = scope is RequestScope { Readable: true };
        var bareColumns = scope is not RuleScope;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var previous = index > 0 ? tokens[index - 1] : default;
            var opens = index + 1 < tokens.Count && tokens[index + 1].Kind == SqlTokenKind.Open;
            string? part;
            if (scope is RuleScope rules && token.Kind == SqlTokenKind.Word && index + 1 < tokens.Count && tokens[index + 1].Kind == SqlTokenKind.Dot)
            {
                var reference = Reference(tokens, ref index, rules, previous.Is("IN"), lists.Contains(true), parameters);
                if (reference is not string compiled)
                {
                    return reference;
                }

                part = compiled;
            }
            else if (scope is RuleScope && previous.Is("IN") && token.Kind != SqlTokenKind.Open)
            {
                return Refused("IN takes a parenthesized list or one auth.claims list");
            }
            else
            {
                part = token.Kind switch
                {
                    SqlTokenKind.String or SqlTokenKind.Number or SqlTokenKind.Blob or SqlTokenKind.Comma => where[token.Start..token.End],
                    SqlTokenKind.Open => "(",
                    SqlTokenKind.Close => ")",
                    SqlTokenKind.Symbol when token.Value[0] is '=' or '<' or '>' or '!' or '+' or '-' or '*' or '/' or '%' or '|' or '&' or '~' => token.Value,
                    SqlTokenKind.Word when previous.Is("AS") => Types.Contains(token.Value) ? token.Value.ToUpperInvariant() : null,
                    SqlTokenKind.Word when previous.Is("COLLATE") => Collations.Contains(token.Value) ? token.Value.ToUpperInvariant() : null,
                    SqlTokenKind.Word when opens && Functions.Contains(token.Value) => token.Value.ToLowerInvariant(),
                    SqlTokenKind.Word when Operators.Contains(token.Value) => token.Value.ToUpperInvariant(),
                    SqlTokenKind.Word or SqlTokenKind.Quoted when bareColumns && !opens && Column(scope.Columns, token.Value) is { } column => "+" + Quote(column),
                    _ => null,
                };
            }

            if (part is null && readable && token.Kind is SqlTokenKind.Word or SqlTokenKind.Quoted && !opens && !previous.Is("AS")
                && !previous.Is("COLLATE") && !Operators.Contains(token.Value))
            {
                return ColumnNotReadable(token.Value);
            }

            if (part is null)
            {
                return Refused(token.Kind switch
                {
                    SqlTokenKind.Parameter => "where takes no parameters",
                    SqlTokenKind.Semicolon => "where is one expression",
                    SqlTokenKind.Dot when bareColumns => "where names this table's columns without a qualifier",
                    SqlTokenKind.Word or SqlTokenKind.Quoted when !bareColumns && !opens => $"{token.Value} must be qualified as row.{token.Value} or new.{token.Value}",
                    SqlTokenKind.Word when opens => $"where cannot call {token.Value}; it allows only deterministic functions without side effects",
                    SqlTokenKind.Word or SqlTokenKind.Quoted => $"where cannot use {token.Value}; it allows this table's columns, literals and operators, without subqueries",
                    _ => $"where cannot use {token.Value}",
                });
            }

            switch (token.Kind)
            {
                case SqlTokenKind.Open:
                    lists.Add(previous.Is("IN"));
                    break;
                case SqlTokenKind.Close when lists.Count == 0:
                    return Refused("where has unbalanced parentheses");
                case SqlTokenKind.Close:
                    lists.RemoveAt(lists.Count - 1);
                    break;
            }

            if (text.Length > 0 && !(token.Kind == SqlTokenKind.Symbol && previous.Kind == SqlTokenKind.Symbol && previous.End == token.Start))
            {
                text.Append(' ');
            }

            text.Append(part);
        }

        return lists.Count == 0 ? new CompiledCondition(text.ToString(), parameters) : Refused("where has unbalanced parentheses");
    }

    private static object Reference(List<SqlToken> tokens, ref int index, RuleScope scope, bool listPosition, bool inList,
        List<ConditionParameter> parameters)
    {
        var head = tokens[index].Value;
        List<SqlToken> path = [];
        var at = index;
        while (at + 2 < tokens.Count && tokens[at + 1].Kind == SqlTokenKind.Dot && tokens[at + 2].IsName)
        {
            path.Add(tokens[at + 2]);
            at += 2;
        }

        if (path.Count == 0 || at + 1 < tokens.Count && tokens[at + 1].Kind is SqlTokenKind.Dot or SqlTokenKind.Open)
        {
            return Refused($"{head}. starts no reference this condition can use");
        }

        index = at;
        var name = string.Join('.', path.Select(token => token.Value));
        var reference = head + "." + name;
        var operation = scope.Operation;
        var isClaim = head == "auth" && path is [{ Kind: SqlTokenKind.Word, Value: "claims" }, { Kind: SqlTokenKind.Word } claim] && IsClaimName(claim.Value);
        if (listPosition && !isClaim || inList && isClaim)
        {
            return Refused(listPosition ? "IN takes a parenthesized list or one auth.claims list" : $"{reference} is a list only as the whole right side of IN");
        }

        switch (head)
        {
            case "row" or "new" when path.Count == 1:
                if (Column(scope.Columns, path[0].Value) is not { } column)
                {
                    return new Validation($"{reference}: the table has no column {path[0].Value}", "unknown_column");
                }

                var allowed = head == "row"
                    ? operation is RulesOperation.Read or RulesOperation.Update or RulesOperation.Delete
                    : operation is RulesOperation.Create or RulesOperation.Update or RulesOperation.Validate;
                if (!allowed)
                {
                    return NotAllowed(head + ".*", operation);
                }

                return head == "row" && operation == RulesOperation.Update
                    ? Bound(parameters, new ConditionParameter("$row_" + Enumerable.Range(0, scope.Columns.Count).First(at => scope.Columns[at] == column), ConditionSource.Row, column))
                    : "+" + Quote(column);
            case "auth" when path is [{ Kind: SqlTokenKind.Word } field]:
                return field.Value switch
                {
                    "uid" => Bound(parameters, new ConditionParameter("$auth_uid", ConditionSource.Uid)),
                    "org_id" => Bound(parameters, new ConditionParameter("$auth_org_id", ConditionSource.OrgId)),
                    "org_role" => Bound(parameters, new ConditionParameter("$auth_org_role", ConditionSource.OrgRole)),
                    "is_anonymous" => Bound(parameters, new ConditionParameter("$auth_is_anonymous", ConditionSource.IsAnonymous)),
                    _ => Refused($"{reference} is not an auth value; use auth.uid, auth.org_id, auth.org_role, auth.is_anonymous or auth.claims.<name>"),
                };
            case "auth" when isClaim && listPosition:
                var list = Bound(parameters, new ConditionParameter("$auth_list_" + path[1].Value, ConditionSource.ClaimList, path[1].Value));
                return $"(SELECT value FROM json_each({list}) UNION ALL SELECT NULL WHERE {list} IS NULL)";
            case "auth" when isClaim:
                return Bound(parameters, new ConditionParameter("$auth_claim_" + path[1].Value, ConditionSource.Claim, path[1].Value));
            case "request" when name == "time":
                return operation == RulesOperation.Read
                    ? NotAllowed("request.time", operation)
                    : Bound(parameters, new ConditionParameter("$request_time", ConditionSource.RequestTime));
            case "request" when name == "ulid":
                return new Validation("request.ulid is allowed only as a set.create value", "reference_not_allowed");
            default:
                return Refused($"{reference} is not a reference; use row.<column>, new.<column>, auth.<value> or request.time");
        }
    }

    private static Validation NotAllowed(string reference, RulesOperation operation) =>
        new($"{reference} is not allowed in {operation.ToString().ToLowerInvariant()}", "reference_not_allowed");

    private static string Bound(List<ConditionParameter> parameters, ConditionParameter parameter)
    {
        if (!parameters.Exists(existing => existing.Name == parameter.Name))
        {
            parameters.Add(parameter);
        }

        return parameter.Name;
    }

    private static string? Column(IReadOnlyList<string> columns, string name)
    {
        foreach (var column in columns)
        {
            if (string.Equals(column, name, StringComparison.OrdinalIgnoreCase))
            {
                return column;
            }
        }

        return null;
    }

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
