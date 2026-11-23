using System.Diagnostics;
using System.Numerics;
using System.Text;

namespace TerraScale.Platform.Features.Functions;

public static class FunctionStatsMath
{
    public const int HistogramBuckets = 16;
    public const long RetainedSeconds = 8 * 24 * 3600;

    public static string HistogramColumns(string prefix) =>
        string.Join(", ", Enumerable.Range(0, HistogramBuckets).Select(index => prefix + "h" + index));

    public static string HistogramDefinitions() =>
        string.Join(", ", Enumerable.Range(0, HistogramBuckets).Select(static index => "h" + index + " INTEGER NOT NULL DEFAULT 0"));

    public static int Bucket(long durationMs) =>
        durationMs <= 1 ? 0 : Math.Min(HistogramBuckets - 1, BitOperations.Log2((ulong)(durationMs - 1)) + 1);

    public static (long Seconds, int Count) Window(string window) => window switch
    {
        "1h" => (60, 60),
        "24h" => (3600, 24),
        "7d" => (3600, 168),
        _ => throw new UnreachableException(),
    };

    public static long Percentile(IReadOnlyList<long> histogram, long invocations, long maxMs, int percent)
    {
        if (invocations == 0)
        {
            return 0;
        }

        var rank = invocations / 100 * percent + (invocations % 100 * percent + 99) / 100;
        var count = 0L;
        for (var index = 0; index < histogram.Count; index++)
        {
            count += histogram[index];
            if (count >= rank)
            {
                return Math.Min(maxMs, index == HistogramBuckets - 1 ? long.MaxValue : 1L << index);
            }
        }

        return maxMs;
    }

    public static SqlHttpStatement Upsert(FunctionTarget target, string function, FunctionLogBody log)
    {
        var minute = log.At.ToUnixTimeSeconds() / 60 * 60;
        var bucket = "h" + Bucket(log.DurationMs);
        var sql = new StringBuilder()
            .Append("INSERT INTO platform_function_stats (database_id, function_name, minute, invocations, errors, max_ms, sql_calls, rows_read, rows_written, ")
            .Append(bucket)
            .Append(") VALUES (?, ?, ?, 1, ?, ?, ?, ?, ?, 1) ON CONFLICT (database_id, function_name, minute) DO UPDATE SET ")
            .Append("invocations = invocations + 1, errors = errors + excluded.errors, max_ms = max(max_ms, excluded.max_ms), ")
            .Append("sql_calls = sql_calls + excluded.sql_calls, rows_read = rows_read + excluded.rows_read, rows_written = rows_written + excluded.rows_written, ")
            .Append(bucket).Append(" = ").Append(bucket).Append(" + 1")
            .ToString();
        return SqlValues.Statement(
            sql,
            SqlValues.Text(target.Database.ToString()),
            SqlValues.Text(function),
            SqlValues.Number(minute),
            SqlValues.Number(log.Error is null ? 0 : 1),
            SqlValues.Number(log.DurationMs),
            SqlValues.Number(log.SqlCalls),
            SqlValues.Number(log.RowsRead),
            SqlValues.Number(log.RowsWritten));
    }

    public static SqlHttpStatement Read(FunctionTarget target, string function, string window, DateTimeOffset now)
    {
        var (seconds, count) = Window(window);
        var first = now.ToUnixTimeSeconds() / seconds * seconds - (count - 1) * seconds;
        var sql = "SELECT (minute / " + seconds + ") * " + seconds + " AS slot, SUM(invocations), SUM(errors), MAX(max_ms), SUM(sql_calls), SUM(rows_read), SUM(rows_written), "
            + string.Join(", ", Enumerable.Range(0, HistogramBuckets).Select(static index => "SUM(h" + index + ")"))
            + " FROM platform_function_stats WHERE database_id = ? AND function_name = ? AND minute >= ? GROUP BY slot";
        return SqlValues.Statement(sql, SqlValues.Text(target.Database.ToString()), SqlValues.Text(function), SqlValues.Number(first));
    }

    public static FunctionStats Aggregate(string window, SqlHttpStatementResult rows, DateTimeOffset now)
    {
        var (seconds, count) = Window(window);
        var first = now.ToUnixTimeSeconds() / seconds * seconds - (count - 1) * seconds;
        var slots = rows.RowsOf().ToDictionary(static row => row.Number(0));
        var total = new long[HistogramBuckets];
        long invocations = 0, errors = 0, maxMs = 0, sqlCalls = 0, rowsRead = 0, rowsWritten = 0;
        var buckets = new List<FunctionStatsBucket>(count);
        for (var index = 0; index < count; index++)
        {
            var at = first + index * seconds;
            if (!slots.TryGetValue(at, out var row))
            {
                buckets.Add(new FunctionStatsBucket(DateTimeOffset.FromUnixTimeSeconds(at), 0, 0, 0, 0));
                continue;
            }

            var histogram = Enumerable.Range(0, HistogramBuckets).Select(bucket => row.Number(7 + bucket)).ToArray();
            var slotInvocations = row.Number(1);
            var slotMax = row.Number(3);
            for (var bucket = 0; bucket < HistogramBuckets; bucket++)
            {
                total[bucket] += histogram[bucket];
            }

            invocations += slotInvocations;
            errors += row.Number(2);
            maxMs = Math.Max(maxMs, slotMax);
            sqlCalls += row.Number(4);
            rowsRead += row.Number(5);
            rowsWritten += row.Number(6);
            buckets.Add(new FunctionStatsBucket(
                DateTimeOffset.FromUnixTimeSeconds(at),
                slotInvocations,
                row.Number(2),
                Percentile(histogram, slotInvocations, slotMax, 50),
                Percentile(histogram, slotInvocations, slotMax, 95)));
        }

        return new FunctionStats(
            window,
            invocations,
            errors,
            Percentile(total, invocations, maxMs, 50),
            Percentile(total, invocations, maxMs, 95),
            Percentile(total, invocations, maxMs, 99),
            maxMs,
            sqlCalls,
            rowsRead,
            rowsWritten,
            buckets);
    }
}
