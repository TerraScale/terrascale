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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SqlSuggestion>? Suggestions = null, bool Write = false);

public static partial class SqlValidation
{
    // Each run is one TerraBase request, and every request is its own transaction.
    private static readonly HashSet<string> Transaction = ["BEGIN", "COMMIT", "END", "ROLLBACK", "SAVEPOINT", "RELEASE"];
    private static readonly HashSet<string> Writes = ["INSERT", "UPDATE", "DELETE", "REPLACE", "UPSERT"];

    public static IReadOnlyList<SqlDiagnostic> Diagnostics(string statement)
    {
        var error = Error(statement);
        if (error.Length > 0) return [new(error, "error", 0, statement.Length)];
        var result = new List<SqlDiagnostic>();
        var visible = Visible(statement)!;
        foreach (Match parameter in AnyParameterPattern().Matches(visible)) result.Add(new("The console cannot bind parameters such as $1; replace them with values.", "error", parameter.Index, parameter.Index + parameter.Length));
        return result;
    }

    // Reads run with read access only; anything else (writes, DDL) needs write access, and TerraBase enforces the same split.
    public static bool IsRead(string statement)
    {
        var words = Visible(statement) is { } visible ? WordPattern().Matches(visible).Select(match => match.Value.ToUpperInvariant()).ToArray() : [];
        return words.FirstOrDefault() is "SELECT" or "VALUES" or "EXPLAIN" || words.FirstOrDefault() == "WITH" && !words.Any(Writes.Contains);
    }

    public static SqlAnalysis Analyze(string statement)
    {
        statement = statement.Trim();
        var error = Diagnostics(statement).FirstOrDefault(item => item.Severity == "error")?.Message ?? "";
        if (error.Length > 0) return new(false, error);
        return new(true, Write: !IsRead(statement));
    }

    public static string Error(string statement)
    {
        if (string.IsNullOrWhiteSpace(statement)) return "SQL must not be empty.";
        if (Encoding.UTF8.GetByteCount(statement) > 16 * 1024) return "SQL must be 16 KiB or less.";
        var visible = Visible(statement);
        if (visible is null) return "Enter one complete SQL statement; each run executes a single statement.";
        if (WordPattern().Match(visible) is { Success: true } first && Transaction.Contains(first.Value.ToUpperInvariant()))
            return "Transaction statements (BEGIN, COMMIT, ROLLBACK) are not accepted: each run is its own transaction.";
        return "";
    }

    private static string? Visible(string statement)
    {
        var chars = statement.ToCharArray();
        char quote = '\0';
        var ended = false;
        for (var index = 0; index < chars.Length; index++)
        {
            var character = chars[index];
            if (quote != '\0')
            {
                chars[index] = ' ';
                if (character == quote)
                {
                    if (quote != ']' && index + 1 < chars.Length && chars[index + 1] == quote) chars[++index] = ' ';
                    else { chars[index] = ' '; quote = '\0'; }
                }
                continue;
            }
            if (index + 1 < chars.Length && character == '-' && chars[index + 1] == '-')
            { while (index < chars.Length && chars[index] != '\n') chars[index++] = ' '; continue; }
            if (index + 1 < chars.Length && character == '/' && chars[index + 1] == '*')
            {
                var close = statement.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (close < 0) return null;
                for (; index < close + 2; index++) chars[index] = ' ';
                index--; continue;
            }
            if (ended && !char.IsWhiteSpace(character)) return null;
            if (char.IsControl(character) && !char.IsWhiteSpace(character)) return null;
            if (character is '\'' or '"' or '`' or '[') { quote = character == '[' ? ']' : character; chars[index] = ' '; }
            else if (character == ';') { ended = true; chars[index] = ' '; }
        }
        return quote == '\0' ? new string(chars) : null;
    }
    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)] private static partial Regex WordPattern();
    [GeneratedRegex(@"\$[A-Za-z0-9_]+|\?[0-9]*|[:@][A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)] private static partial Regex AnyParameterPattern();
}
