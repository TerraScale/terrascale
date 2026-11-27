using System.Text.Json;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed record DatabaseTableSummary(string Name, IReadOnlyList<DatabaseSqlColumn> Columns, long Rows, IReadOnlyList<DatabaseTableColumn>? ColumnDetails = null)
    {
        public IReadOnlyList<DatabaseTableColumn> Schema => ColumnDetails ?? [.. Columns.Select(column => new DatabaseTableColumn(column.Name, column.Decltype))];
    }

    public sealed record DatabaseTables(IReadOnlyList<DatabaseTableSummary> Tables, bool Truncated)
    {
        public const int PreviewRows = 100;

        public long Rows => Tables.Aggregate(0L, (sum, table) => checked(sum + table.Rows));

        public static async Task<DatabaseTables> LoadAsync(IDatabaseBackend backend, DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            var catalog = await backend.SqlTablesAsync(scope, home, databaseId, cancellationToken);
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var chunk in catalog.Tables.Chunk(20))
            {
                var sql = string.Join(" UNION ALL ", chunk.Select(table => "SELECT " + Literal(table.Name) + ", count(*) FROM " + Identifier(table.Name)));
                var result = await backend.SqlAsync(scope, home, databaseId, sql, false, cancellationToken);
                foreach (var row in result.Rows)
                {
                    if (row is not [{ ValueKind: JsonValueKind.String } name, { ValueKind: JsonValueKind.Number } count])
                    {
                        throw new HttpRequestException("TerraBase returned an invalid row count.");
                    }

                    counts[name.GetString()!] = count.GetInt64();
                }
            }

            return new([.. catalog.Tables.Select(table => new DatabaseTableSummary(table.Name, table.Columns, counts.GetValueOrDefault(table.Name), table.ColumnDetails))], catalog.Truncated);
        }

        public static async Task<long> CountAsync(IDatabaseBackend backend, DatabaseScope scope, DatabaseRegion home, string databaseId, string table, CancellationToken cancellationToken)
        {
            var result = await backend.SqlAsync(scope, home, databaseId, "SELECT count(*) FROM " + Identifier(table), false, cancellationToken);
            return result.Rows is [[{ ValueKind: JsonValueKind.Number } count]] && count.TryGetInt64(out var rows)
                ? rows : throw new HttpRequestException("TerraBase returned an invalid row count.");
        }

        public static Task<DatabaseSqlResult> PreviewAsync(IDatabaseBackend backend, DatabaseScope scope, DatabaseRegion home, string databaseId, string table, CancellationToken cancellationToken)
        {
            return backend.SqlAsync(scope, home, databaseId, "SELECT * FROM " + Identifier(table) + " LIMIT " + PreviewRows.ToString(System.Globalization.CultureInfo.InvariantCulture), false, cancellationToken);
        }

        public static string CompactType(string? decltype)
        {
            if (string.IsNullOrWhiteSpace(decltype) || decltype == "No declared type")
            {
                return "—";
            }

            var type = decltype.Trim().ToUpperInvariant();
            var paren = type.IndexOf('(', StringComparison.Ordinal);
            var name = (paren < 0 ? type : type[..paren]).Trim();
            return name switch
            {
                "INTEGER" or "INT" or "BIGINT" or "SMALLINT" or "TINYINT" or "MEDIUMINT" or "INT2" or "INT8" or "UNSIGNED BIG INT" => "INT",
                "BOOLEAN" or "BOOL" => "BOOL",
                "CHARACTER VARYING" or "VARCHAR" or "VARYING CHARACTER" or "NVARCHAR" or "NCHAR" or "NATIVE CHARACTER" or "CHARACTER" or "CHAR" or "CLOB" or "TEXT" => "TEXT",
                "DOUBLE PRECISION" or "DOUBLE" or "FLOAT" or "REAL" => "REAL",
                "DECIMAL" or "NUMERIC" => "NUM",
                "DATETIME" or "TIMESTAMP" => "TIME",
                _ => name,
            };
        }

        public static bool NumericType(string? decltype)
        {
            return decltype is { Length: > 0 } type && (type.Contains("INT", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("REAL", StringComparison.OrdinalIgnoreCase) || type.Contains("FLOA", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("DOUB", StringComparison.OrdinalIgnoreCase) || type.Contains("NUMERIC", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("DECIMAL", StringComparison.OrdinalIgnoreCase));
        }

        internal static string Identifier(string name)
        {
            return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        private static string Literal(string value)
        {
            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }
    }
}
