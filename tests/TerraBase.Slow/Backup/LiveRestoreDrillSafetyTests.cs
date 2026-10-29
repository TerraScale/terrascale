using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Engine.Turso;
using TerraBase.LiveRestoreDrill;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Slow.Backup;

public sealed class LiveRestoreDrillSafetyTests
{
    [Test]
    public async Task Replay_preserves_captured_state_and_detects_damage(CancellationToken token)
    {
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var database = await api.TenantOfAsync(cancellationToken: token);
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, token))!;
        var scratch = Directory.CreateTempSubdirectory("terrabase-drill-checksum-").FullName;
        try
        {
            await using var tap = (await engine.OpenTapAsync(database, token))!;
            using var log = new MemoryStream();
            var replayed = Path.Combine(scratch, "replayed.db");
            var reference = Path.Combine(scratch, "reference.db");
            await tap.SnapshotAsync(log, replayed, token);
            log.SetLength(0);
            log.Position = 0;
            var written = await api.PutAsync(DocumentApi.NewId(), new { value = "captured" }, cancellationToken: token);
            await Assert.That((int)written.Status).IsBetween(200, 299);
            var latest = await tap.SnapshotAsync(log, reference, token);
            await Assert.That(latest.Read.Bytes).IsGreaterThan(0);
            log.Position = 0;
            await Assert.That(await engine.RollForwardAsync(replayed, SegmentsAsync(), token)).IsTrue();
            var expected = Convert.ToHexString(await engine.RestoreDrillChecksumAsync(reference, token));
            await Assert.That(Convert.ToHexString(await engine.RestoreDrillChecksumAsync(replayed, token))).IsEqualTo(expected);

            foreach (var mutation in new[]
            {
                "UPDATE _tb_state SET stamp = stamp + 1;",
                "UPDATE _tb_state SET h1 = h1 + 1;",
                "UPDATE commit_offset SET value = value + 1;",
            })
            {
                var damaged = Path.Combine(scratch, "damaged.db");
                File.Copy(replayed, damaged, overwrite: true);
                File.Copy(replayed + DatabaseKeys.Suffix, damaged + DatabaseKeys.Suffix, overwrite: true);
                Execute(damaged, mutation);
                await Assert.That(Convert.ToHexString(await engine.RestoreDrillChecksumAsync(damaged, token))).IsNotEqualTo(expected).Because(mutation);
            }

            async IAsyncEnumerable<Stream> SegmentsAsync()
            {
                yield return log;
                await Task.CompletedTask;
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cancellation_after_checksum_dispatch_aborts_the_scan_and_releases_admission(bool drill, CancellationToken token)
    {
        var scratch = Directory.CreateTempSubdirectory("terrabase-drill-cancel-").FullName;
        using var meter = new Meter("drill-cancellation-test");
        var engine = CreateEngine(scratch, meter);
        try
        {
            var snapshot = Path.Combine(scratch, "scan.db");
            TestMasterKey.Create(snapshot);
            Execute(snapshot, """
                CREATE TABLE payload (value BLOB);
                WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i + 1 FROM n WHERE i < 4096)
                INSERT INTO payload SELECT zeroblob(8192) FROM n;
                """);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var checksum = drill
                ? engine.RestoreDrillChecksumAsync(snapshot, stop.Token).AsTask()
                : engine.ChecksumAsync(snapshot, stop.Token).AsTask();
            while (engine.Io.Running == 0 && !checksum.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            await Assert.That(checksum.IsCompleted).IsFalse().Because("cancel after the worker starts, not while queued");
            await stop.CancelAsync();
            await Assert.That(async () => await checksum).Throws<OperationCanceledException>();
            await Assert.That(engine.Io.Admitted).IsEqualTo(0);
        }
        finally
        {
            await ScratchCleanup.DeleteAsync(engine, scratch);
        }
    }

    [Test]
    public async Task Scratch_deletion_waits_for_native_cleanup_after_disposal_grace_expires()
    {
        var scratch = Directory.CreateTempSubdirectory("terrabase-drill-cleanup-").FullName;
        using var meter = new Meter("drill-cleanup-test");
        var engine = CreateEngine(scratch, meter);
        engine.Io.Admit();
        Task? cleanup = null;
        try
        {
            await engine.DisposeAsync();
            cleanup = ScratchCleanup.DeleteAsync(engine, scratch);
            await Assert.That(engine.CleanupCompletion.IsCompleted).IsFalse();
            await Assert.That(cleanup.IsCompleted).IsFalse();
            await Assert.That(Directory.Exists(scratch)).IsTrue();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(scratch)).IsTrue();
        }
        finally
        {
            engine.Io.Release();
            await (cleanup ?? ScratchCleanup.DeleteAsync(engine, scratch));
        }

        await Assert.That(engine.CleanupCompletion.IsCompletedSuccessfully).IsTrue();
        await Assert.That(Directory.Exists(scratch)).IsFalse();
    }

    [Test]
    public async Task Native_cleanup_failure_is_propagated_and_retains_scratch()
    {
        var scratch = Directory.CreateTempSubdirectory("terrabase-drill-cleanup-error-").FullName;
        using var meter = new Meter("drill-cleanup-error-test");
        var engine = CreateEngine(scratch, meter);
        try
        {
            File.WriteAllBytes(Path.Combine(scratch, ReplicaIdentityFile.FileName), [0]);
            await Assert.That(async () => await ScratchCleanup.DeleteAsync(engine, scratch)).Throws<AggregateException>();
            await Assert.That(engine.CleanupCompletion.IsFaulted).IsTrue();
            await Assert.That(Directory.Exists(scratch)).IsTrue();
        }
        finally
        {
            try
            {
                await engine.CleanupCompletion;
            }
            catch (AggregateException)
            {
            }

            Directory.Delete(scratch, recursive: true);
        }
    }

    private static TursoStorageEngine CreateEngine(string scratch, Meter meter) =>
        new(scratch, 1, 32, 64L << 20, meter, NullLogger<TursoStorageEngine>.Instance, new TestMasterKey(), TimeSpan.Zero);

    private static void Execute(string file, string sql)
    {
        using var connection = new SqliteConnection(DocumentApi.ConnectionString(file));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
