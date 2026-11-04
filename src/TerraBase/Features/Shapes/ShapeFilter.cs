using System.Text;
using TerraBase.Engine;
using TerraBase.Errors;

namespace TerraBase.Features.Shapes;

public sealed record ShapeFilter(string? Where, IReadOnlyList<string> Columns)
{
    public string Key => (Where ?? "") + "\0" + string.Join('\u001f', Columns);
}

internal sealed record ShapeQuery(string? Where, string? Columns, ShapeGrantRule? Grant = null)
{
    internal static readonly ShapeQuery None = new(null, null);

    internal bool IsEmpty => Where is null && Columns is null && Grant is null;
}

internal sealed record ShapeGrantRule(string? Where, string? Columns);

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

        if (query.Grant?.Columns is { } granted)
        {
            var allowed = granted.Split(',').Select(name => Column(tableColumns, name.Trim())).OfType<string>().ToHashSet(StringComparer.Ordinal);
            columns = [.. columns.Where(allowed.Contains)];
            if (columns.Count == 0)
            {
                return new Forbidden("the read grant allows none of the requested columns", "column_not_granted");
            }
        }

        string? condition = null;
        if (query.Where is { } where)
        {
            var checkedWhere = Where(where, tableColumns);
            if (checkedWhere is not string text)
            {
                return checkedWhere;
            }

            condition = text;
        }

        if (query.Grant?.Where is { } rule)
        {
            if (Where(rule, tableColumns) is not string checkedRule)
            {
                return new Forbidden("the table's read grant no longer applies to its columns", "grant_invalid");
            }

            condition = condition is null ? checkedRule : $"({checkedRule}) AND ({condition})";
        }

        return new ShapeFilter(condition, columns);
    }

    private static object Columns(string list, IReadOnlyList<string> tableColumns)
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

    internal static string? Bind(string where, string subject)
    {
        if (!SqlText.TryTokenize(where, out var tokens))
        {
            return null;
        }

        var text = new StringBuilder();
        var at = 0;
        foreach (var token in tokens.Where(token => token.Kind == SqlTokenKind.Parameter))
        {
            if (token.Value != Subject)
            {
                return null;
            }

            text.Append(where, at, token.Start - at).Append('\'').Append(subject.Replace("'", "''", StringComparison.Ordinal)).Append('\'');
            at = token.End;
        }

        return text.Append(where, at, where.Length - at).ToString();
    }

    internal const string Subject = ":sub";

    private static object Where(string where, IReadOnlyList<string> tableColumns)
    {
        if (where.Length > MaxWhere || !SqlText.TryTokenize(where, out var tokens) || tokens.Count == 0)
        {
            return Refused($"where must be a complete expression of at most {MaxWhere} characters");
        }

        var text = new StringBuilder();
        var depth = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var previous = index > 0 ? tokens[index - 1] : default;
            var opens = index + 1 < tokens.Count && tokens[index + 1].Kind == SqlTokenKind.Open;
            string? part = token.Kind switch
            {
                SqlTokenKind.String or SqlTokenKind.Number or SqlTokenKind.Blob or SqlTokenKind.Comma => where[token.Start..token.End],
                SqlTokenKind.Open => "(",
                SqlTokenKind.Close => ")",
                SqlTokenKind.Symbol when token.Value[0] is '=' or '<' or '>' or '!' or '+' or '-' or '*' or '/' or '%' or '|' or '&' or '~' => token.Value,
                SqlTokenKind.Word when previous.Is("AS") => Types.Contains(token.Value) ? token.Value.ToUpperInvariant() : null,
                SqlTokenKind.Word when previous.Is("COLLATE") => Collations.Contains(token.Value) ? token.Value.ToUpperInvariant() : null,
                SqlTokenKind.Word when opens && Functions.Contains(token.Value) => token.Value.ToLowerInvariant(),
                SqlTokenKind.Word when Operators.Contains(token.Value) => token.Value.ToUpperInvariant(),
                SqlTokenKind.Word or SqlTokenKind.Quoted when !opens && Column(tableColumns, token.Value) is { } column => "+" + Quote(column),
                _ => null,
            };
            if (part is null)
            {
                return Refused(token.Kind switch
                {
                    SqlTokenKind.Parameter => "where takes no parameters",
                    SqlTokenKind.Semicolon => "where is one expression",
                    SqlTokenKind.Dot => "where names this table's columns without a qualifier",
                    SqlTokenKind.Word when opens => $"where cannot call {token.Value}; it allows only deterministic functions without side effects",
                    SqlTokenKind.Word or SqlTokenKind.Quoted => $"where cannot use {token.Value}; it allows this table's columns, literals and operators, without subqueries",
                    _ => $"where cannot use {token.Value}",
                });
            }

            depth += token.Kind switch { SqlTokenKind.Open => 1, SqlTokenKind.Close => -1, _ => 0 };
            if (depth < 0)
            {
                return Refused("where has unbalanced parentheses");
            }

            if (text.Length > 0 && !(token.Kind == SqlTokenKind.Symbol && previous.Kind == SqlTokenKind.Symbol && previous.End == token.Start))
            {
                text.Append(' ');
            }

            text.Append(part);
        }

        return depth == 0 ? text.ToString() : Refused("where has unbalanced parentheses");
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
