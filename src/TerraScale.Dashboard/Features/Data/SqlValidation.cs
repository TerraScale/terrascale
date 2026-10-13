using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.Features.Data;

public sealed record SqlDiagnostic(string Message, string Severity, int From, int To);
public sealed record SqlSuggestion(string Field, string Reason);
public sealed record SqlAnalysis(bool Ok,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Collection = null,
    int ParameterSlots = 0, bool HasLimit = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Explain = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SqlSuggestion>? Suggestions = null);

public static partial class SqlValidation
{
    private static readonly HashSet<string> Forbidden = ["INSERT", "UPDATE", "DELETE", "REPLACE", "CREATE", "DROP", "ALTER", "ATTACH", "DETACH", "GRANT", "REVOKE", "BEGIN", "COMMIT", "ROLLBACK", "VACUUM", "PRAGMA", "UPSERT", "TRUNCATE", "SAVEPOINT", "TRANSACTION", "REINDEX", "ANALYZE", "INTO", "END"];
    private static readonly HashSet<string> UnsafeFunctions = ["random", "randomblob", "changes", "total_changes", "last_insert_rowid", "load_extension", "readfile", "writefile", "datetime", "date", "time", "strftime", "julianday", "unixepoch", "current_date", "current_time", "current_timestamp", "sqlite_version", "sqlite_source_id", "sqlite_compileoption_get", "sqlite_compileoption_used", "pragma_table_info", "pragma_index_info", "pragma_index_list", "json_each", "json_tree"];
    public static IReadOnlyList<SqlDiagnostic> Diagnostics(string statement)
    {
        var error = Error(statement);
        if (error.Length > 0) return [new(error, "error", 0, statement.Length)];
        var result = new List<SqlDiagnostic>();
        var visible = Visible(statement)!;
        foreach (Match parameter in AnyParameterPattern().Matches(visible)) result.Add(new("The console cannot bind parameters such as $1; replace them with values.", "error", parameter.Index, parameter.Index + parameter.Length));
        foreach (Match word in WordPattern().Matches(visible))
        {
            var keyword = word.Value.ToUpperInvariant();
            if (keyword is "JOIN" or "WITH" or "UNION" or "GROUP" or "HAVING" or "OFFSET" or "EXPLAIN" or "DISTINCT")
                result.Add(new(keyword + " is not supported by TerraBase's SQL dialect.", "warning", word.Index, word.Index + word.Length));
        }
        return result;
    }

    public static SqlAnalysis Analyze(string statement)
    {
        statement = statement.Trim();
        var error = Diagnostics(statement).FirstOrDefault(item => item.Severity == "error")?.Message ?? "";
        if (error.Length > 0) return new(false, error);
        var visible = Visible(statement)!;
        if (AnyParameterPattern().Matches(visible).Any(match => !ParameterPattern().IsMatch(match.Value) || match.Value == "$0"))
            return new(false, "SQL runtime check rejected this statement: named and positional parameters require bindings.");
        var collection = Regex.Match(visible, @"\bFROM\s+([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var suggestions = new List<SqlSuggestion>();
        foreach (Match match in Regex.Matches(visible, @"\bWHERE\s+([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            suggestions.Add(new(match.Groups[1].Value, "WHERE filters on " + match.Groups[1].Value + "; consider an index for this field."));
        foreach (Match match in Regex.Matches(visible, @"\bORDER\s+BY\s+([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            suggestions.Add(new(match.Groups[1].Value, "ORDER BY sorts on " + match.Groups[1].Value + "; consider an index for bounded pagination."));
        return new(true, Collection: collection.Success ? collection.Groups[1].Value : null, ParameterSlots: ParameterPattern().Matches(visible).Count,
            HasLimit: Regex.IsMatch(visible, @"\bLIMIT\s+[0-9]+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            Explain: visible.TrimStart().StartsWith("EXPLAIN ", StringComparison.OrdinalIgnoreCase) ? statement : "EXPLAIN QUERY PLAN " + statement,
            Suggestions: suggestions.Count == 0 ? null : suggestions);
    }

    public static string Error(string statement)
    {
        if (string.IsNullOrWhiteSpace(statement)) return "SQL must not be empty.";
        if (Encoding.UTF8.GetByteCount(statement) > 16 * 1024) return "SQL must be 16 KiB or less.";
        var visible = Visible(statement);
        if (visible is null) return "Enter one read-only SQL statement without comments.";
        var words = WordPattern().Matches(visible).Select(match => match.Value.ToUpperInvariant()).ToArray();
        if (words.Length == 0 || words[0] is not ("SELECT" or "WITH" or "EXPLAIN") || words.Any(Forbidden.Contains)) return "Only SELECT, WITH, and EXPLAIN statements are allowed.";
        if (words[0] == "WITH" && (words.ElementAtOrDefault(1) == "RECURSIVE" || !words.Contains("SELECT"))) return "WITH must contain a non-recursive SELECT statement.";
        if (words[0] == "EXPLAIN" && !(visible.TrimStart().StartsWith("EXPLAIN QUERY PLAN ", StringComparison.OrdinalIgnoreCase) || visible.TrimStart().StartsWith("EXPLAIN SELECT ", StringComparison.OrdinalIgnoreCase))) return "EXPLAIN may only use EXPLAIN QUERY PLAN or EXPLAIN SELECT.";
        if (words[0] == "EXPLAIN" && !Regex.IsMatch(visible.TrimStart(), @"^EXPLAIN\s+(QUERY\s+PLAN\s+)?(SELECT|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "EXPLAIN may only use a read-only SELECT or WITH statement.";
        // These are the pure ts-go SQL checks, before any provider execution.
        var identifiers = Visible(statement, true)!;
        foreach (Match identifier in WordPattern().Matches(identifiers))
        {
            var word = identifier.Value.ToLowerInvariant();
            var next = identifier.Index + identifier.Length;
            while (next < identifiers.Length && char.IsWhiteSpace(identifiers[next])) next++;
            if (word.StartsWith("sqlite_", StringComparison.Ordinal) || word.StartsWith("pragma_", StringComparison.Ordinal)) return "Internal SQLite names and pragma functions are not allowed.";
            if (Forbidden.Contains(word.ToUpperInvariant())) return "Only SELECT, WITH, and EXPLAIN statements are allowed.";
            if (UnsafeFunctions.Contains(word) && (next < identifiers.Length && identifiers[next] == '(' || word is "current_date" or "current_time" or "current_timestamp")) return "Non-deterministic or unsafe SQL functions are not allowed.";
        }
        return "";
    }

    private static string? Visible(string statement, bool identifiers = false)
    {
        var chars = statement.ToCharArray();
        char quote = '\0';
        var ended = false;
        for (var index = 0; index < chars.Length; index++)
        {
            var character = chars[index];
            if (quote != '\0')
            {
                if (!identifiers || quote == '\'') chars[index] = ' ';
                if (character == quote)
                {
                    if (quote != ']' && index + 1 < chars.Length && chars[index + 1] == quote) chars[++index] = ' ';
                    else { chars[index] = ' '; quote = '\0'; }
                }
                continue;
            }
            if (ended && !char.IsWhiteSpace(character)) return null;
            if (char.IsControl(character) && !char.IsWhiteSpace(character)) return null;
            if (index + 1 < chars.Length && (character == '-' && chars[index + 1] == '-' || character == '/' && chars[index + 1] == '*')) return null;
            if (character is '\'' or '"' or '`' or '[') { quote = character == '[' ? ']' : character; chars[index] = ' '; }
            else if (character == ';') { ended = true; chars[index] = ' '; }
        }
        return quote == '\0' ? new string(chars) : null;
    }
    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)] private static partial Regex WordPattern();
    [GeneratedRegex(@"\$[0-9]+\b", RegexOptions.CultureInvariant)] private static partial Regex ParameterPattern();
    [GeneratedRegex(@"\$[A-Za-z0-9_]+|\?[0-9]*|[:@][A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)] private static partial Regex AnyParameterPattern();
}
