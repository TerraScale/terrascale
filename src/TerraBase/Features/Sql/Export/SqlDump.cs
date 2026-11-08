using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using TerraBase.Engine;

namespace TerraBase.Features.Sql;

internal static class SqlDump
{
    private const int PageRows = 500;

    internal static async ValueTask WriteAsync(IStorageTransaction transaction, PipeWriter output, CancellationToken cancellationToken)
    {
        Write(output, "BEGIN;\n");
        var tables = await RowsAsync(transaction,
            "SELECT name, sql FROM sqlite_schema WHERE type = 'table' AND sql IS NOT NULL AND sql NOT LIKE 'CREATE VIRTUAL%' ORDER BY name", cancellationToken).ConfigureAwait(false);
        foreach (var row in tables.Rows)
        {
            if (row[0] is not string name || row[1] is not string create || SqlScript.IsReservedName(name))
            {
                continue;
            }

            Write(output, Statement(create));
            await WriteRowsAsync(transaction, name, output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var indexes = await RowsAsync(transaction,
            "SELECT name, sql FROM sqlite_schema WHERE type = 'index' AND sql IS NOT NULL ORDER BY name", cancellationToken).ConfigureAwait(false);
        foreach (var row in indexes.Rows)
        {
            if (row[0] is not string name || row[1] is not string create || SqlScript.IsReservedName(name))
            {
                continue;
            }

            Write(output, Statement(create));
        }

        Write(output, "COMMIT;\n");
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<SqlStatementResult> RowsAsync(IStorageTransaction transaction, string sql, CancellationToken cancellationToken) =>
        await transaction.ExecuteStatementAsync(SqlSession.Internal(sql), cancellationToken).ConfigureAwait(false);

    private static string Statement(string create) => create.TrimEnd(' ', '\r', '\n', '\t', ';') + ";\n";

    private static async ValueTask WriteRowsAsync(IStorageTransaction transaction, string table, PipeWriter output, CancellationToken cancellationToken)
    {
        var quoted = "\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var after = long.MinValue;
        while (true)
        {
            var page = await transaction.ExecuteStatementAsync(
                SqlSession.Internal(string.Create(CultureInfo.InvariantCulture, $"SELECT rowid, * FROM {quoted} WHERE rowid > {after} ORDER BY rowid LIMIT {PageRows}"))
                    with { MaxRows = PageRows },
                cancellationToken).ConfigureAwait(false);
            if (page.Truncated && page.Rows.Count < PageRows)
            {
                throw new InvalidOperationException("an export page exceeded its byte budget");
            }

            for (var i = 0; i < page.Rows.Count; i++)
            {
                Write(output, "INSERT INTO ");
                Write(output, quoted);
                Write(output, " VALUES (");
                var row = page.Rows[i];
                for (var column = 1; column < row.Length; column++)
                {
                    if (column > 1)
                    {
                        Write(output, ",");
                    }

                    WriteValue(output, row[column]);
                }

                Write(output, ");\n");
            }

            if (page.Rows.Count < PageRows || page.Rows[^1][0] is not long last)
            {
                return;
            }

            after = last;
        }
    }

    private static void WriteValue(PipeWriter output, object? value)
    {
        switch (value)
        {
            case null:
                Write(output, "NULL");
                break;
            case long integer:
                Write(output, string.Create(CultureInfo.InvariantCulture, $"{integer}"));
                break;
            case double real:
                Write(output, real.ToString("R", CultureInfo.InvariantCulture));
                break;
            case string text:
                Write(output, "'");
                Write(output, text.Replace("'", "''", StringComparison.Ordinal));
                Write(output, "'");
                break;
            case byte[] blob:
                Write(output, "X'");
                Write(output, Convert.ToHexString(blob));
                Write(output, "'");
                break;
            default:
                throw new InvalidOperationException("a column value is not null, an integer, a real, text or a blob");
        }
    }

    private static void Write(IBufferWriter<byte> output, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        output.Write(bytes);
    }
}
