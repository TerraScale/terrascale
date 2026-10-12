namespace TerraBase.Engine;

/// <summary>
/// Names a client collection cannot take: its table would collide with the engine's own tables, or with SQLite's and
/// Turso's (docs/terrabase/multi-region-replication.md section 5).
/// </summary>
public static class CollectionName
{
    // shortcut: collection metadata and commit offsets still live in plain-named engine tables; drop each name when its
    // table goes or moves under _tb_.
    private static readonly string[] Tables = ["collections", "commit_offset"];

    /// <summary>Whether <paramref name="name"/> is reserved.</summary>
    public static bool IsReserved(string name) =>
        name.StartsWith("sqlite_", StringComparison.Ordinal) || name.StartsWith("turso_", StringComparison.Ordinal) || Tables.Contains(name);
}
