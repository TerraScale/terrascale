using System.Globalization;
using System.Text;
using TerraBase.Features.Qos;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Sqlite;

/// <remarks>
/// A relational read (docs/terrabase/sql/relational-sql.md section 4): each table becomes a CTE of the same name over its
/// collection's table, written <c>main."t"</c> so the CTE shadows it, with one <c>json_extract</c> column per field and the
/// scan guard that charges the work budget. Column names are identifiers, so they embed safely.
/// shortcut: CTEs are not flattened by Turso, so every read scans each referenced table and no index serves it, and the
/// scan guard runs once per table row, not per join combination: a cross join's fan-out is bounded only by the statement
/// and the 10,000 result rows. Rewrite columns to json_extract over the tables (index-served, guard in the join loops) when a
/// customer table passes about 150,000 rows or joins need their combinations charged.
/// </remarks>
internal sealed partial class SqliteStorageTransaction
{
    // SQLite and Turso messages quote the statement; only these phrases of them are reported.
    private static readonly string[] SqlFailures =
    [
        "no such column", "no such table", "no such function", "ambiguous column", "syntax error", "misuse of aggregate",
        "misuse of window function", "circular reference", "wrong number of arguments", "datatype mismatch", "too many",
        "integer overflow", "not supported", "must be",
    ];

    public ValueTask<SqlProgramResult> ExecuteSqlAsync(SqlProgram program, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(program);
        return RunAsync(() => WithQueryBudget(() => RunProgram(program, cancellationToken), cancellationToken), cancellationToken);
    }

    // Worker-only.
    private SqlProgramResult RunProgram(SqlProgram program, CancellationToken cancellationToken)
    {
        var ctes = program.Tables.Select(table => string.Create(CultureInfo.InvariantCulture,
            $"{Table(table.Name)} AS (SELECT {(table.WithId ? "id AS \"_id\", " : "")}{string.Join(", ", table.Columns.Select(column => $"json_extract(doc, '$.{column}') AS \"{column}\""))} FROM main.{Table(table.Name)} WHERE tb_qos_scan(octet_length(doc)))")).ToList();
        if (program.Ctes is { } own)
        {
            ctes.Add(own);
        }

        var sql = new StringBuilder();
        if (ctes.Count > 0)
        {
            sql.Append("WITH ").AppendJoin(", ", ctes).Append(' ');
        }

        using var command = session.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql.Append(program.Statement).ToString();
        for (var i = 0; i < program.Parameters.Count; i++)
        {
            command.Parameters.AddWithValue("$p" + (i + 1).ToString(CultureInfo.InvariantCulture), program.Parameters[i] ?? DBNull.Value);
        }

        _queryBudget!.Charge(command.CommandText);
        CountRead("query");
        try
        {
            using var reader = command.ExecuteReader();
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<object?[]>();
            long bytes = 0;
            long reserved = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_queryBudget.Exceeded)
                {
                    throw new QueryCapacityException("query execution budget exceeded");
                }

                var row = new object?[columns.Count];
                long size = 0;
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = reader.GetValue(i) switch
                    {
                        DBNull => null,
                        var value => value,
                    };
                    size += row[i] switch
                    {
                        string text => Encoding.UTF8.GetByteCount(text),
                        byte[] blob => blob.Length,
                        _ => 8,
                    };
                }

                if (rows.Count == program.MaxRows || bytes + size > program.MaxBytes)
                {
                    // Reset rather than drain: the rest of the statement is not needed.
                    reader.Abort();
                    return new SqlProgramResult(columns, rows, Truncated: true);
                }

                // Strings in memory take two bytes a character, each value a box or reference; reserved in blocks, so
                // ten thousand rows do not hold ten thousand leases.
                var held = checked((2 * size) + (16L * row.Length) + 64);
                if (held > reserved)
                {
                    var block = Math.Max(held, 256 * 1024);
                    QosContext.ReserveResult(block, store.IsSystem ? null : QosContext.Current);
                    reserved += block;
                }

                reserved -= held;
                bytes += size;
                rows.Add(row);
            }

            return new SqlProgramResult(columns, rows, Truncated: false);
        }
        catch (SqliteException error) when (!SqliteSession.Stopped(error))
        {
            var phrase = SqlFailures.FirstOrDefault(failure => error.Message.Contains(failure, StringComparison.OrdinalIgnoreCase));
            throw new SqlProgramException(phrase ?? "the statement failed to run", error);
        }
    }
}
