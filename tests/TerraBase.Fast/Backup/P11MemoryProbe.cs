using System.Globalization;
using Turso;
using TerraBase.Engine.Turso;

namespace TerraBase.Fast.Backup;

// WIP P11 probe (throwaway, to be removed): one memory-heavy step on a large database file (P11_DB), then its peak RSS.
// The malloc_trim measurement used a DllImport variant that is not kept (no warning suppressions in the repository).
public sealed class P11MemoryProbe
{
    [Test]
    public async Task Probe()
    {
        var source = Environment.GetEnvironmentVariable("P11_DB");
        var step = Environment.GetEnvironmentVariable("P11_STEP");
        if (source is null || step is null)
        {
            return;
        }

        var started = DateTime.UtcNow;
        switch (step)
        {
            case "copy":
            case "copy2":
                {
                    for (var round = 0; round < (step == "copy2" ? 2 : 1); round++)
                    {
                        var target = Environment.GetEnvironmentVariable("P11_OUT")! + round;
                        using var connection = new TursoConnection($"Data Source={source}");
                        connection.Open();
                        using (var begin = connection.CreateCommand())
                        {
                            begin.CommandText = "BEGIN;";
                            begin.ExecuteNonQuery();
                        }

                        TursoCopy.Write(connection, target, CancellationToken.None);
                        Console.WriteLine("P11 round " + round + " " + string.Join(" ", File.ReadAllLines("/proc/self/status").Where(line => line.StartsWith("VmRSS", StringComparison.Ordinal) || line.StartsWith("VmHWM", StringComparison.Ordinal))));
                    }

                    break;
                }
            case "checksum":
            case "integrity":
                {
                    using var connection = new Turso.Data.Sqlite.SqliteConnection($"Data Source={source}");
                    connection.Open();
                    using var command = connection.CreateCommand();
                    if (step == "integrity")
                    {
                        command.CommandText = "PRAGMA integrity_check;";
                        Console.WriteLine(command.ExecuteScalar());
                        break;
                    }

                    var tables = new List<string>();
                    command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name;";
                    using (var rows = command.ExecuteReader())
                    {
                        while (rows.Read())
                        {
                            if (!ChangeLog.EngineTable(rows.GetString(0)))
                            {
                                tables.Add(rows.GetString(0));
                            }
                        }
                    }

                    long count = 0;
                    foreach (var table in tables)
                    {
                        using var shape = connection.CreateCommand();
                        shape.CommandText = $"SELECT * FROM \"{table}\" LIMIT 0;";
                        int columns;
                        using (var reader = shape.ExecuteReader())
                        {
                            columns = reader.FieldCount;
                        }

                        shape.CommandText = $"SELECT * FROM \"{table}\" ORDER BY {string.Join(", ", Enumerable.Range(1, columns).Select(c => c.ToString(CultureInfo.InvariantCulture)))};";
                        using var all = shape.ExecuteReader();
                        while (all.Read())
                        {
                            count++;
                        }
                    }

                    Console.WriteLine($"rows={count}");
                    break;
                }
        }

        var status = await File.ReadAllLinesAsync("/proc/self/status");
        Console.WriteLine($"P11 step={step} seconds={(DateTime.UtcNow - started).TotalSeconds:F1} " + string.Join(" ", status.Where(line => line.StartsWith("VmHWM", StringComparison.Ordinal) || line.StartsWith("VmRSS", StringComparison.Ordinal) || line.StartsWith("RssAnon", StringComparison.Ordinal)).Select(line => string.Join("", line.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries)))));
    }
}
