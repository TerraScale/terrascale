using System.Diagnostics;
using Bogus;
using TerraBase.Testing;

namespace TerraBase.E2E.PitrRepro;

/// <summary>
/// Suspected defect 3: <c>ContinuousBackup.ShipAsync</c> drops a pending log over 256 MiB and starts a new generation with a
/// full snapshot, and that snapshot's upload runs inline in the file's shipping job. Writes committed while the upload is
/// slow pile up in the change log; the next read finds more than 256 MiB, abandons the new generation, and the next pass
/// uploads a snapshot again. A slow snapshot upload is simulated by holding the database's snapshot PUTs while the test
/// writes just over 256 MiB of change log (about 280 MB of 900 KB document updates per cycle).
/// </summary>
[Category("pitr-repro")]
public sealed class BacklogLivelockRepro
{
    private const int Cycles = 3;
    private const int WritesPerCycle = 310;
    private const int DocumentChars = 900_000;
    private const long MaxPendingBytes = 256L * 1024 * 1024;

    [Test]
    [Scenario(Nodes.InProcess)]
    [Timeout(30 * 60_000)]
    public async Task A_backlog_over_256_MiB_after_a_slow_snapshot_upload_abandons_every_new_generation(CancellationToken cancellationToken)
    {
        ReproHost.SkipUnlessRequested();
        await using var host = await ReproHost.StartAsync();
        var database = await host.Api.CreateManagedDatabaseAsync("main");
        // Hobby's request budget (50/s, refilled on the frozen test clock) would refuse the load; Enterprise has none.
        await host.Api.SetPlanAsync(host.Api.User, database.Organization, "enterprise", cancellationToken);
        using var data = await host.Api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "load", cancellationToken);
        var faker = new Faker();
        var texts = Enumerable.Range(0, 8).Select(_ => new string(faker.Random.Chars('a', 'z', DocumentChars))).ToArray();
        var upload = new SlowUpload($"{ReproHost.File(database)}/snapshots/");
        host.S3.BeforePut = upload.BeforePutAsync;
        try
        {
            await RunCyclesAsync(host, database, data, texts, upload, cancellationToken);
        }
        catch (Exception error)
        {
            ReproHost.Log($"failed: {error}");
            throw;
        }
        finally
        {
            // Shutdown ships every file once more; never hold that upload.
            host.S3.BeforePut = null;
            upload.Release();
        }
    }

    private static async Task RunCyclesAsync(ReproHost host, ManagedDatabaseRef database, HttpClient data, string[] texts, SlowUpload upload,
        CancellationToken cancellationToken)
    {
        var file = ReproHost.File(database);
        for (var cycle = 1; cycle <= Cycles; cycle++)
        {
            upload.Arm();
            host.Time.Advance(TimeSpan.FromSeconds(10));
            var pass = host.Backup.RunOnceAsync(cancellationToken);
            var snapshotKey = await upload.Entered.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);

            // Sustained writes while the snapshot upload is in flight.
            var clock = Stopwatch.StartNew();
            for (var write = 0; write < WritesPerCycle; write++)
            {
                await ReproHost.PutAsync(data, database, "load", $"doc-{write % 8}", texts[write % texts.Length], cancellationToken);
            }

            ReproHost.Log($"cycle {cycle}: snapshot PUT {snapshotKey[(snapshotKey.LastIndexOf('/') + 1)..]} held while {WritesPerCycle} x {DocumentChars} B updates "
                + $"({WritesPerCycle * (long)DocumentChars / (1024 * 1024)} MiB) committed in {clock.Elapsed.TotalSeconds:F1} s");
            upload.Release();
            await pass;
            var established = host.Status(database);
            ReproHost.Log($"cycle {cycle}: snapshot stored -> {host.StatusLine(database)}");
            await Assert.That(established.Generation).IsNotNull();

            // The next pass reads the backlog.
            clock.Restart();
            await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
            var after = host.Status(database);
            var segments = host.S3.Objects.Keys.Count(key => key.StartsWith($"{file}/wal/", StringComparison.Ordinal));
            ReproHost.Log($"cycle {cycle}: next pass ({clock.Elapsed.TotalSeconds:F1} s) -> {host.StatusLine(database)}; segment objects of the file={segments}; "
                + $"{await host.CountersAsync(cancellationToken)}; working set={Environment.WorkingSet / (1024 * 1024)} MiB");
            ReproHost.Log($"cycle {cycle}: window {await host.WindowAsync(database, cancellationToken)}");
            await Assert.That(after.LogBytes).IsGreaterThan(MaxPendingBytes);
            await Assert.That(after.Generation).IsNull();
            await Assert.That(after.PendingBytes).IsEqualTo(0);
            await Assert.That(after.LastError).IsNull();
            await Assert.That(segments).IsEqualTo(0);
        }

        var snapshots = host.S3.Objects.Keys.Count(key => key.StartsWith($"{file}/snapshots/", StringComparison.Ordinal));
        var counters = await host.CountersAsync(cancellationToken);
        ReproHost.Log($"after {Cycles} cycles: snapshot objects of the file={snapshots}, snapshot PUTs held={upload.Holds}, segment objects=0; {counters}");
        await Assert.That(snapshots).IsEqualTo(Cycles);
        // Silent: no failure and no hole was ever counted; only generations_total climbs.
        await Assert.That(counters).DoesNotContain("terrabase_backup_failures");
        await Assert.That(counters).DoesNotContain("terrabase_backup_holes");
    }

    // Holds each PUT under the prefix until the test releases it: a snapshot upload that takes as long as the test's writes.
    private sealed class SlowUpload(string prefix)
    {
        private TaskCompletionSource<string> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _holds;

        public Task<string> Entered => Volatile.Read(ref _entered).Task;

        public int Holds => Volatile.Read(ref _holds);

        public void Arm()
        {
            Volatile.Write(ref _entered, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref _release, new(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public void Release() => Volatile.Read(ref _release).TrySetResult();

        public async Task BeforePutAsync(string key, CancellationToken cancellationToken)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return;
            }

            Interlocked.Increment(ref _holds);
            Volatile.Read(ref _entered).TrySetResult(key);
            await Volatile.Read(ref _release).Task.WaitAsync(cancellationToken);
        }
    }
}
