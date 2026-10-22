using System.Diagnostics;
using System.Globalization;
using Turso.Data.Sqlite;

namespace TerraBase.E2E.WalFaults;

/// <summary>
/// <see cref="WalSyncFaults"/> itself, on a process of its own that commits one row per transaction to a Turso database
/// in WAL mode with <c>synchronous = FULL</c>, so every commit syncs the log (<see cref="WalSyncFaultHost"/>).
/// </summary>
public sealed class WalSyncFaultTests
{
    private const int SigKillExit = 128 + 9;

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_delayed_sync_holds_every_commit_for_at_least_the_delay()
    {
        using var run = Run.Create();
        run.Faults.Delay(TimeSpan.FromMilliseconds(700));
        await Assert.That(await run.HostAsync(rows: 3)).IsEqualTo(0).Because(run.Output);

        var commits = run.Commits();
        await Assert.That(commits.Count).IsEqualTo(3);
        await Assert.That(commits.Min(commit => commit.Milliseconds)).IsGreaterThanOrEqualTo(700).Because("every commit syncs the log once at least");
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_kill_at_a_sync_stops_the_process_before_it_and_keeps_what_it_wrote()
    {
        using var run = Run.Create();
        run.Faults.KillAtSync(4);
        await Assert.That(await run.HostAsync(rows: 20)).IsEqualTo(SigKillExit).Because(run.Output);

        // A process kill is not a power loss: the commit stopped at its sync was written, and the page cache keeps it.
        var committed = run.Commits().Count;
        await Assert.That(committed).IsLessThan(20);
        await Assert.That(await run.RowsAsync()).IsBetween(committed, committed + 1);
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_power_loss_after_a_kill_at_a_sync_keeps_exactly_the_commits_that_returned()
    {
        using var run = Run.Create();
        run.Faults.KillAtSync(4);
        await Assert.That(await run.HostAsync(rows: 20)).IsEqualTo(SigKillExit).Because(run.Output);

        var wal = run.Database + "-wal";
        var synced = run.Faults.SyncedLength(wal);
        await Assert.That(synced).IsGreaterThan(0);
        await Assert.That(new FileInfo(wal).Length).IsGreaterThan(synced).Because("the commit stopped at its sync wrote frames past the last sync");
        run.Faults.DropUnsynced(run.Data);

        await Assert.That(new FileInfo(wal).Length).IsEqualTo(synced);
        await Assert.That(await run.RowsAsync()).IsEqualTo(run.Commits().Count);
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_failed_sync_fails_the_commit_and_the_database_takes_writes_again_after_a_restart()
    {
        using var run = Run.Create();
        await Assert.That(await run.HostAsync(rows: 1)).IsEqualTo(0).Because(run.Output);

        run.Faults.Fail(5);
        _ = await run.HostAsync(rows: 1);
        await Assert.That(File.ReadAllText(run.Progress)).Contains("failed ").Because(run.Output);

        run.Faults.Clear();
        await Assert.That(await run.HostAsync(rows: 1)).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Commits().Count).IsEqualTo(2);
        await Assert.That(await run.RowsAsync()).IsGreaterThanOrEqualTo(2);
    }

    private sealed class Run : IDisposable
    {
        private readonly string _root;

        private Run(string root)
        {
            _root = root;
            _ = Directory.CreateDirectory(Data);
            Faults = new WalSyncFaults(Path.Combine(root, "faults"));
        }

        public WalSyncFaults Faults { get; }

        public string Data => Path.Combine(_root, "data");

        public string Database => Path.Combine(Data, "host.db");

        public string Progress => Database + ".progress";

        public string Output { get; private set; } = "";

        public static Run Create()
        {
            Skip.When(Cluster.Live, "runs a local process");
            Skip.When(WalSyncFaults.Library is null, "needs Linux and cc to build the write-ahead log fault shim");
            return new Run(Directory.CreateTempSubdirectory("walfaults-").FullName);
        }

        public async Task<int> HostAsync(int rows)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TerraBase.E2E.dll"));
            start.ArgumentList.Add("--treenode-filter");
            start.ArgumentList.Add($"/*/*/{nameof(WalSyncFaultHost)}/*");
            start.ArgumentList.Add("--results-directory");
            start.ArgumentList.Add(Path.Combine(_root, "results"));
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TERRABASE_", StringComparison.Ordinal)).ToList())
            {
                _ = start.Environment.Remove(name);
            }

            start.Environment[WalSyncFaultHost.DatabaseVariable] = Database;
            start.Environment[WalSyncFaultHost.RowsVariable] = rows.ToString(CultureInfo.InvariantCulture);
            Faults.AddTo(start.Environment);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("the host process did not start");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Output = await output + await errors;
            return process.ExitCode;
        }

        public List<(int Row, long Milliseconds)> Commits() =>
            [.. File.ReadLines(Progress).Where(line => !line.StartsWith("failed ", StringComparison.Ordinal))
                .Select(line => line.Split(' ')).Select(parts => (int.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture)))];

        public async Task<long> RowsAsync()
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM rows";
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}

/// <summary>
/// The process <see cref="WalSyncFaultTests"/> faults: commits its rows one per transaction and appends
/// "&lt;row&gt; &lt;milliseconds&gt;" to <c>&lt;database&gt;.progress</c> after each commit returns, or "failed &lt;error&gt;"
/// and stops. Without its variables it skips: it is a host, not a scenario.
/// </summary>
[Category("wal-fault-host")]
public sealed class WalSyncFaultHost
{
    internal const string DatabaseVariable = "WALFAULTS_HOST_DATABASE";
    internal const string RowsVariable = "WALFAULTS_HOST_ROWS";

    [Test]
    // The child process the WAL fault tests start; the faults are the shim's, which no Faults flag names.
    [Scenario(Nodes.Processes)]
    public async Task Commits_one_row_per_transaction_each_synced()
    {
        var database = Environment.GetEnvironmentVariable(DatabaseVariable);
        Skip.When(string.IsNullOrEmpty(database), "the process of WalSyncFaultTests: started by it with the fault shim loaded");
        var rows = int.Parse(Environment.GetEnvironmentVariable(RowsVariable)!, CultureInfo.InvariantCulture);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; CREATE TABLE IF NOT EXISTS rows (n INTEGER PRIMARY KEY);";
        _ = await command.ExecuteNonQueryAsync();

        command.CommandText = "INSERT INTO rows (n) VALUES (NULL)";
        for (var row = 1; row <= rows; row++)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                _ = await command.ExecuteNonQueryAsync();
            }
            catch (SqliteException exception)
            {
                await File.AppendAllTextAsync(database + ".progress", $"failed {exception.Message.ReplaceLineEndings(" ")}\n");
                return;
            }

            await File.AppendAllTextAsync(database + ".progress", string.Create(CultureInfo.InvariantCulture, $"{row} {clock.ElapsedMilliseconds}\n"));
        }
    }
}
