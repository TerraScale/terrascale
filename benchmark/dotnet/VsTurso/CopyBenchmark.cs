using System.Diagnostics;
using System.Globalization;
using Bogus;
using TerraBase.Engine.Turso;
using TerraBase.Testing;
using Turso;
using Turso.Data.Sqlite;

namespace TerraBase.E2E
{
    /// <summary>Encrypted logical-copy throughput; run on an idle benchmark host, independently of parity cells.</summary>
    [Category("benchmark-host")]
    public sealed class CopyBenchmark
    {
        [Test]
        public async Task Measures_prepared_copy(CancellationToken cancellationToken)
        {
            var output = Environment.GetEnvironmentVariable("TERRABASE_TURSO_COPY_OUT");
            Skip.When(string.IsNullOrEmpty(output), "host benchmark: set TERRABASE_TURSO_COPY_OUT");
            const int count = 100_000;
            var directory = Directory.CreateTempSubdirectory("turso-copy-bench-");
            try
            {
                var path = Path.Combine(directory.FullName, "source.db");
                TestMasterKey.Create(path);
                using (var source = new SqliteConnection(TestMasterKey.ConnectionString(path)))
                {
                    source.Open();
                    using var command = source.CreateCommand();
                    command.CommandText = "CREATE TABLE docs (id INTEGER PRIMARY KEY, body TEXT);";
                    command.ExecuteNonQuery();
                    using var transaction = source.BeginTransaction();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO docs VALUES ($id, $body);";
                    command.ReuseStatement = true;
                    command.Parameters.AddWithValue("$id", 0L);
                    command.Parameters.AddWithValue("$body", new Faker { Random = new Randomizer(7) }.Random.String2(256));
                    for (var i = 0; i < count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        command.Parameters[0].Value = (long)i;
                        command.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }

                await using var log = new StreamWriter(output!);
                await log.WriteLineAsync($"# host={Environment.MachineName}; rows={count}; encrypted; 3 repeats");
                await log.WriteLineAsync("| Repeat | Seconds | Rows/s | Allocated B/row | Process lifetime peak working set B | Rows copied |");
                await log.WriteLineAsync("| --- | ---: | ---: | ---: | ---: | ---: |");
                for (var repeat = 1; repeat <= 3; repeat++)
                {
                    using var reader = new TursoConnection(TestMasterKey.ConnectionString(path));
                    reader.Open();
                    using var begin = reader.CreateCommand();
                    begin.CommandText = "BEGIN;";
                    begin.ExecuteNonQuery();
                    var target = Path.Combine(directory.FullName, $"copy-{repeat}.db");
                    TestMasterKey.Create(target);
                    var connectionString = TestMasterKey.ConnectionString(target);
                    var allocated = GC.GetTotalAllocatedBytes(precise: true);
                    var started = Stopwatch.GetTimestamp();
                    TursoCopy.Write(reader, connectionString, cancellationToken);
                    var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                    var bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                    using var copy = new SqliteConnection(connectionString);
                    copy.Open();
                    using var check = copy.CreateCommand();
                    check.CommandText = "SELECT count(*) FROM docs;";
                    var copied = (long)check.ExecuteScalar()!;
                    await Assert.That(copied).IsEqualTo(count);
                    using var process = Process.GetCurrentProcess();
                    await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"| {repeat} | {seconds:F3} | {count / seconds:F0} | {bytes / count} | {process.PeakWorkingSet64} | {copied} |"));
                }
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }
    }
}
