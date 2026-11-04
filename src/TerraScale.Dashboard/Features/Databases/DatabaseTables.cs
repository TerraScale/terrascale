using System.Text.Json;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed record DatabaseTableSummary(string Name, IReadOnlyList<DatabaseSqlColumn> Columns, long Rows);

    // The database's SQL tables with their columns and row counts, read through the same SQL path as the editor.
    public sealed record DatabaseTables(IReadOnlyList<DatabaseTableSummary> Tables, bool Truncated)
    {
        public const int PreviewRows = 100;

        public long Rows => Tables.Aggregate(0L, (sum, table) => checked(sum + table.Rows));

        public static async Task<DatabaseTables> LoadAsync(IDatabaseBackend backend, DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            var catalog = await backend.SqlTablesAsync(scope, home, databaseId, cancellationToken);
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            // One read statement counts up to 20 tables; the catalog already stops at 100.
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

            return new([.. catalog.Tables.Select(table => new DatabaseTableSummary(table.Name, table.Columns, counts.GetValueOrDefault(table.Name)))], catalog.Truncated);
        }

        public static Task<DatabaseSqlResult> PreviewAsync(IDatabaseBackend backend, DatabaseScope scope, DatabaseRegion home, string databaseId, string table, CancellationToken cancellationToken)
        {
            return backend.SqlAsync(scope, home, databaseId, "SELECT * FROM " + Identifier(table) + " LIMIT " + PreviewRows.ToString(System.Globalization.CultureInfo.InvariantCulture), false, cancellationToken);
        }

        private static string Identifier(string name)
        {
            return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        private static string Literal(string value)
        {
            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }
    }
}
