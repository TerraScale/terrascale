using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TerraBase.Testing;

/// <summary>Reads SQLite query plans from a database file a test server created.</summary>
public static partial class QueryPlan
{
    /// <summary>
    /// Runs <c>EXPLAIN QUERY PLAN</c> for <paramref name="sql"/> on the database at
    /// <paramref name="databasePath"/>, read-only, and returns the plan lines joined by <c> | </c>
    /// (empty when the statement has no plan, like a plain insert).
    /// </summary>
    public static async Task<string> ExplainAsync(string databasePath, string sql)
    {
        // Unpooled, so the file is released when this returns and the server can delete it.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        // Plans do not depend on parameter values, but every parameter must be bound.
        foreach (var name in Parameter().Matches(sql).Select(match => match.Value).Distinct(StringComparer.Ordinal))
        {
            command.Parameters.AddWithValue(name, DBNull.Value);
        }

        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // Columns: id, parent, notused, detail.
            lines.Add(reader.GetString(3));
        }

        return string.Join(" | ", lines);
    }

    [GeneratedRegex(@"\$\w+", RegexOptions.CultureInvariant)]
    private static partial Regex Parameter();
}
