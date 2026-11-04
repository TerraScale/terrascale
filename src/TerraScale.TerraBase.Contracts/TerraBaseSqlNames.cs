namespace TerraScale.TerraBase.Contracts
{
    /// <summary>The table names TerraBase keeps for its engine: SQL that names them is refused, and table listings leave them out.</summary>
    public static class TerraBaseSqlNames
    {
        private static readonly string[] Prefixes = ["_tb_", "_ts_", "turso_", "__turso_", "tb_index_", "fts_dir_", "pragma_", "sqlite_stat"];

        private static readonly string[] Tables = ["_collections", "_indexes", "_unique", "collections", "commit_offset"];

        private static readonly HashSet<string> ReservedTables = new(Tables, StringComparer.OrdinalIgnoreCase);

        /// <summary>The prefixes of reserved table names, lower case.</summary>
        public static IReadOnlyList<string> ReservedPrefixes => Prefixes;

        /// <summary>The engine's own table names, lower case.</summary>
        public static IReadOnlyList<string> ReservedTableNames => Tables;

        /// <summary>Whether SQL may not name <paramref name="name"/> as a table, string or PRAGMA argument.</summary>
        public static bool IsReserved(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            return ReservedTables.Contains(name) || Prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Whether a table is the engine's or SQLite's own, never one a user created.</summary>
        public static bool IsInternalTable(string name)
        {
            return IsReserved(name) || name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);
        }
    }
}
