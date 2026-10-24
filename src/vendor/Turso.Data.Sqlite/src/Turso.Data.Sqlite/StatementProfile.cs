using System.Collections.Concurrent;
using System.Diagnostics;

namespace Turso.Data.Sqlite;

// TEMPORARY diagnostic (RT2d Q-PARITY, never merged): count and time per statement text.
public static class StatementProfile
{
    public static readonly ConcurrentDictionary<string, long[]> Stats = new(StringComparer.Ordinal);

    public static Scope Time(string? sql) => new(sql ?? "", Stopwatch.GetTimestamp());

    public readonly struct Scope(string sql, long started) : IDisposable
    {
        public void Dispose()
        {
            var entry = Stats.GetOrAdd(sql, static _ => new long[2]);
            Interlocked.Increment(ref entry[0]);
            Interlocked.Add(ref entry[1], Stopwatch.GetTimestamp() - started);
        }
    }
}
