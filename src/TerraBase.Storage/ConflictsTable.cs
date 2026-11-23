using System.Collections.Frozen;
using System.Text;

namespace TerraBase.Storage;

internal static class ConflictsTable
{
    private static readonly string[] Columns =
    [
        "id", "kind", "table_name", "row_key", "column_name", "winner_value", "loser_value",
        "winner_region", "winner_node", "winner_hlc", "winner_transaction_id", "winner_state",
        "loser_region", "loser_node", "loser_hlc", "loser_transaction_id", "loser_state", "message", "created_at",
    ];

    private static readonly string View = string.Concat(
        "(SELECT ",
        string.Join(", ", Columns.Select(static column => string.Concat("CAST(NULL AS TEXT) AS ", column))),
        " WHERE 0)");

    private static readonly FrozenSet<string> WriteTargets = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "INTO", "UPDATE", "TABLE", "ON");

    private static readonly FrozenSet<string> Clauses = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "WHERE", "ORDER", "GROUP", "HAVING", "LIMIT", "OFFSET", "WINDOW", "UNION", "EXCEPT", "INTERSECT", "JOIN", "INNER", "LEFT",
        "RIGHT", "FULL", "CROSS", "NATURAL", "ON", "USING", "RETURNING", "INDEXED", "NOT");

    public static SqliteStatus Expose(string sql, out string exposed)
    {
        exposed = sql;
        if (!sql.Contains("conflicts", StringComparison.OrdinalIgnoreCase))
        {
            return SqliteStatus.Success;
        }

        var tokens = SqlTokenizer.Tokenize(sql);
        StringBuilder? builder = null;
        var copied = 0;
        for (var index = 0; index + 2 < tokens.Count; index++)
        {
            if (!tokens[index].Names("terrascale") || !tokens[index + 1].IsSymbol(".") || !tokens[index + 2].Names("conflicts")
                || (index > 0 && tokens[index - 1].IsSymbol(".")) || (index + 3 < tokens.Count && tokens[index + 3].IsSymbol(".")))
            {
                continue;
            }

            if (index > 0 && (WriteTargets.Contains(tokens[index - 1].Text) || (tokens[index - 1].Is("FROM") && index > 1 && tokens[index - 2].Is("DELETE"))))
            {
                return new SqliteStatus(SqliteNative.Auth, "terrascale.conflicts is read-only");
            }

            builder ??= new StringBuilder(sql.Length + View.Length);
            _ = builder.Append(sql, copied, tokens[index].Start - copied).Append(View);
            if (!Aliased(tokens, index + 3))
            {
                _ = builder.Append(" AS conflicts");
            }

            copied = tokens[index + 2].End;
            index += 2;
        }

        if (builder is not null)
        {
            exposed = builder.Append(sql, copied, sql.Length - copied).ToString();
        }

        return SqliteStatus.Success;
    }

    private static bool Aliased(List<SqlToken> tokens, int index) =>
        index < tokens.Count && tokens[index] is var next
        && (next.Is("AS") || next.Kind == TokenKind.QuotedIdentifier || (next.Kind == TokenKind.Word && !Clauses.Contains(next.Text)));
}
