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
        var tables = await transaction.ExecuteStatementAsync(SqlSession.Internal(
            "SELECT name, sql FROM sqlite_schema WHERE type = 'table' AND sql IS NOT NULL AND sql NOT LIKE 'CREATE VIRTUAL%' ORDER BY name"), cancellationToken).ConfigureAwait(false);
        foreach (var row in tables.Rows)
        {
            if (row[0] is not string name || row[1] is not string create || SqlScript.IsReservedName(name))
            {
                continue;
            }

            Write(output, Statement(create));
            if (create.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase))
            {
                await WriteKeyedRowsAsync(transaction, name, output, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteRowidRowsAsync(transaction, name, output, cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var indexes = await transaction.ExecuteStatementAsync(SqlSession.Internal(
            "SELECT name, sql FROM sqlite_schema WHERE type = 'index' AND sql IS NOT NULL ORDER BY name"), cancellationToken).ConfigureAwait(false);
        foreach (var row in indexes.Rows)
        {
            if (row[0] is not string name || row[1] is not string create || SqlScript.IsReservedName(name))
            {
                continue;
            }

            Write(output, Statement(create));
        }

        await WriteSchemaEntriesAsync(transaction, "view", output, cancellationToken).ConfigureAwait(false);
        await WriteSchemaEntriesAsync(transaction, "trigger", output, cancellationToken).ConfigureAwait(false);

        Write(output, "COMMIT;\n");
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Statement(string create) => create.TrimEnd(' ', '\r', '\n', '\t', ';') + ";\n";

    private static async ValueTask WriteSchemaEntriesAsync(IStorageTransaction transaction, string type, PipeWriter output, CancellationToken cancellationToken)
    {
        var entries = await transaction.ExecuteStatementAsync(SqlSession.Internal(
            $"SELECT name, sql FROM sqlite_schema WHERE type = '{type}' AND sql IS NOT NULL ORDER BY name"), cancellationToken).ConfigureAwait(false);
        foreach (var row in entries.Rows)
        {
            if (row[0] is not string name || row[1] is not string create || SqlScript.IsReservedName(name))
            {
                continue;
            }

            Write(output, Statement(create));
        }
    }

    private static async ValueTask<SqlStatementResult> PageAsync(IStorageTransaction transaction, string sql, IReadOnlyList<object?> args, CancellationToken cancellationToken) =>
        await transaction.ExecuteStatementAsync(SqlSession.Internal(sql) with { Args = args, MaxRows = PageRows }, cancellationToken).ConfigureAwait(false);

    private static void CheckPage(SqlStatementResult page)
    {
        if (page.Truncated && page.Rows.Count < PageRows)
        {
            throw new InvalidOperationException("an export page exceeded its byte budget");
        }
    }

    private static async ValueTask WriteRowidRowsAsync(IStorageTransaction transaction, string table, PipeWriter output, CancellationToken cancellationToken)
    {
        var quoted = Quote(table);
        var after = long.MinValue;
        while (true)
        {
            var page = await PageAsync(transaction, string.Create(CultureInfo.InvariantCulture,
                $"SELECT rowid, * FROM {quoted} WHERE rowid > {after} ORDER BY rowid LIMIT {PageRows}"), [], cancellationToken).ConfigureAwait(false);
            foreach (var row in page.Rows)
            {
                WriteRow(output, quoted, row, first: 1);
            }

            CheckPage(page);
            if (page.Rows.Count < PageRows)
            {
                return;
            }

            if (page.Rows[^1][0] is not long next)
            {
                throw new InvalidOperationException("an export page lost its paging key");
            }

            after = next;
        }
    }

    private static async ValueTask WriteKeyedRowsAsync(IStorageTransaction transaction, string table, PipeWriter output, CancellationToken cancellationToken)
    {
        var quoted = Quote(table);
        var info = await transaction.ExecuteStatementAsync(SqlSession.Internal(
            "PRAGMA table_info(" + quoted + ")"), cancellationToken).ConfigureAwait(false);
        var columns = new List<string>();
        var keys = new SortedList<long, string>();
        foreach (var row in info.Rows)
        {
            if (row[1] is not string name)
            {
                throw new InvalidOperationException("WITHOUT ROWID primary key not found");
            }

            columns.Add(name);
            if (row[5] is long order && order > 0)
            {
                keys[order] = name;
            }
        }

        if (keys.Count == 0)
        {
            throw new InvalidOperationException("WITHOUT ROWID primary key not found");
        }

        var ordinals = new int[keys.Count];
        for (var key = 0; key < keys.Count; key++)
        {
            ordinals[key] = columns.FindIndex(column => column.Equals(keys.Values[key], StringComparison.OrdinalIgnoreCase));
            if (ordinals[key] < 0)
            {
                throw new InvalidOperationException("WITHOUT ROWID primary key not found");
            }
        }

        var list = string.Join(", ", keys.Values.Select(Quote));
        var placeholders = string.Join(", ", keys.Values.Select(_ => "?"));
        IReadOnlyList<object?>? after = null;
        while (true)
        {
            var where = after is null ? "" : $" WHERE ({list}) > ({placeholders})";
            var page = await PageAsync(transaction, $"SELECT * FROM {quoted}{where} ORDER BY {list} LIMIT {PageRows}", after ?? [],
                cancellationToken).ConfigureAwait(false);
            foreach (var row in page.Rows)
            {
                WriteRow(output, quoted, row, first: 0);
            }

            CheckPage(page);
            if (page.Rows.Count < PageRows)
            {
                return;
            }

            after = [.. ordinals.Select(ordinal => page.Rows[^1][ordinal])];
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static void WriteRow(PipeWriter output, string quoted, object?[] row, int first)
    {
        Write(output, "INSERT INTO ");
        Write(output, quoted);
        Write(output, " VALUES (");
        for (var column = first; column < row.Length; column++)
        {
            if (column > first)
            {
                Write(output, ",");
            }

            WriteValue(output, row[column]);
        }

        Write(output, ");\n");
    }

    private static void WriteValue(IBufferWriter<byte> output, object? value)
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
