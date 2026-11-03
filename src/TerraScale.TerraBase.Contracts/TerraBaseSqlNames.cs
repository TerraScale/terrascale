namespace TerraScale.TerraBase.Contracts
{
    /// <summary>The table names TerraBase keeps for its engine: SQL that names them is refused, and table listings leave them out.</summary>
    public static class TerraBaseSqlNames
    {
        private static readonly string[] ReservedPrefixes = ["_tb_", "turso_", "__turso_", "tb_index_", "fts_dir_", "pragma_", "sqlite_stat"];

        private static readonly HashSet<string> ReservedTables = new(["collections", "commit_offset", "_collections", "_indexes", "_unique"], StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether SQL may not name <paramref name="name"/> as a table, string or PRAGMA argument.</summary>
        public static bool IsReserved(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            return ReservedTables.Contains(name) || ReservedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Whether a table is the engine's or SQLite's own, never one a user created.</summary>
        public static bool IsInternalTable(string name)
        {
            return IsReserved(name) || name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);
        }
    }
}
