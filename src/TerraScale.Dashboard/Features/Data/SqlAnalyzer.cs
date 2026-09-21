using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.Features.Data;

// Port of explorer-utils.ts analyzeExplorerSql/suggestExplorerIndexes.
public static partial class SqlAnalyzer
{
    private static readonly string[] ForbiddenKeywords =
    [
        "INSERT", "UPDATE", "DELETE", "REPLACE", "CREATE", "DROP", "ALTER", "ATTACH", "DETACH",
        "GRANT", "REVOKE", "BEGIN", "COMMIT", "ROLLBACK", "VACUUM", "PRAGMA",
    ];

    public static SqlAnalysis Analyze(string statement)
    {
        var trimmed = statement.Trim();
        if (trimmed.Length == 0)
        {
            return SqlAnalysis.Invalid("SQL must not be empty.");
        }

        var upper = trimmed.ToUpperInvariant();
        foreach (var keyword in ForbiddenKeywords)
        {
            if (Keyword().IsMatch(upper) && upper.Contains(keyword))
            {
                return SqlAnalysis.Invalid("Only SELECT, WITH, and EXPLAIN statements are allowed.");
            }
        }

        if (upper.StartsWith("WITH RECURSIVE", StringComparison.Ordinal))
        {
            return SqlAnalysis.Invalid("WITH RECURSIVE is not allowed.");
        }

        if (upper.StartsWith("EXPLAIN", StringComparison.Ordinal) && !upper.StartsWith("EXPLAIN QUERY PLAN", StringComparison.Ordinal) && !upper.StartsWith("EXPLAIN SELECT", StringComparison.Ordinal))
        {
            return SqlAnalysis.Invalid("EXPLAIN may only use EXPLAIN QUERY PLAN or EXPLAIN SELECT.");
        }

        var head = upper.Split(' ', 2)[0];
        if (head is not ("SELECT" or "WITH" or "EXPLAIN"))
        {
            return SqlAnalysis.Invalid("Only SELECT, WITH, and EXPLAIN statements are allowed.");
        }

        var parameterSlots = ParameterSlots().Matches(trimmed).Count;
        var hasLimit = HasLimit().IsMatch(trimmed);
        var collection = FromCollection().Match(trimmed) is { Success: true } from
            ? from.Groups[1].Value
            : null;
        var whereColumns = WhereColumn().Matches(trimmed).Select(m => m.Groups[1].Value).ToArray();
        var orderByColumns = OrderByColumn().Matches(trimmed).Select(m => m.Groups[1].Value).ToArray();

        var suggestions = new List<SqlSuggestion>();
        foreach (var column in whereColumns)
        {
            suggestions.Add(new(column, $"WHERE filters on {column}; add an extracted column index to avoid a full collection scan."));
        }

        foreach (var column in orderByColumns)
        {
            suggestions.Add(new(column, $"ORDER BY sorts on {column}; an index on this column keeps pagination bounded."));
        }

        var isExplain = upper.StartsWith("EXPLAIN", StringComparison.Ordinal);
        var explain = isExplain ? trimmed : $"EXPLAIN QUERY PLAN {trimmed}";
        return new SqlAnalysis(true, null, collection, parameterSlots, hasLimit, explain, [.. suggestions]);
    }

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|REPLACE|CREATE|DROP|ALTER|ATTACH|DETACH|GRANT|REVOKE|BEGIN|COMMIT|ROLLBACK|VACUUM|PRAGMA)\b")]
    private static partial Regex Keyword();

    [GeneratedRegex(@"\$\d+")]
    private static partial Regex ParameterSlots();

    [GeneratedRegex(@"\bLIMIT\s+\d+", RegexOptions.IgnoreCase)]
    private static partial Regex HasLimit();

    [GeneratedRegex(@"\bFROM\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex FromCollection();

    [GeneratedRegex(@"\bWHERE\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex WhereColumn();

    [GeneratedRegex(@"\bORDER\s+BY\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex OrderByColumn();
}
