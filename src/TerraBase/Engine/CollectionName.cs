namespace TerraBase.Engine;

/// <summary>
/// Names a client collection cannot take: its table would collide with the engine's own tables, or with SQLite's and
/// Turso's (docs/terrabase/multi-region-replication.md section 5).
/// </summary>
public static class CollectionName
{
    // shortcut: the tables the shape history (R2c) and the unique slice (R2b) still keep under plain names; drop each name
    // when its table goes or moves under _tb_.
    private static readonly string[] Tables =
        ["collections", "commit_offset", "shape_history", "change_log", "change_log_births", "change_log_high", "unique_keys", "unique_invalid", "unique_conflicts"];

    /// <summary>Whether <paramref name="name"/> is reserved.</summary>
    public static bool IsReserved(string name) =>
        name.StartsWith("sqlite_", StringComparison.Ordinal) || name.StartsWith("turso_", StringComparison.Ordinal) || Tables.Contains(name);
}
