namespace TerraBase.Features.HomeApply;

internal static class ConflictTable
{
    public const string Name = "_ts_conflicts";

    public static readonly (string Column, string Definition)[] Schema =
    [
        ("id", "TEXT PRIMARY KEY"),
        ("kind", "TEXT NOT NULL"),
        ("table_name", "TEXT"),
        ("row_key", "TEXT"),
        ("column_name", "TEXT"),
        ("winner_value", "TEXT"),
        ("loser_value", "TEXT"),
        ("winner_region", "TEXT"),
        ("winner_node", "TEXT"),
        ("winner_hlc", "TEXT"),
        ("winner_transaction_id", "TEXT"),
        ("winner_state", "TEXT"),
        ("loser_region", "TEXT NOT NULL"),
        ("loser_node", "TEXT NOT NULL"),
        ("loser_hlc", "TEXT NOT NULL"),
        ("loser_transaction_id", "TEXT NOT NULL"),
        ("loser_state", "TEXT NOT NULL"),
        ("message", "TEXT NOT NULL"),
        ("created_at", "TEXT NOT NULL"),
    ];

    public static string Ddl =>
        $"CREATE TABLE IF NOT EXISTS {Name} ({string.Join(", ", Schema.Select(part => $"{part.Column} {part.Definition}"))})";

    public static string ColumnList => string.Join(", ", Schema.Select(part => part.Column));

    public static string Insert =>
        $"INSERT INTO {Name} ({ColumnList}) VALUES ({string.Join(", ", Enumerable.Repeat("?", Schema.Length))})";

    public static string EmptyView =>
        $"(SELECT {string.Join(", ", Schema.Select(part => $"NULL AS {part.Column}"))} WHERE 0)";
}
