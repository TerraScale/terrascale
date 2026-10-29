using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

public sealed class HealthyRewriteEdgeTests
{
    private const string Probe = "rewrite_probe";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_rewrite_waits_for_a_held_reader_while_cached_readers_and_writers_go_on(bool system)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        TenantContext? database = null;
        TursoDatabaseStore store;
        if (system)
        {
            store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        }
        else
        {
            (database, store) = await CreateAsync(engine, token);
        }

        await PutAsync(store, StorageFixture.Id("kept"), """{"name":"kept"}""", token);
        var before = store.Incarnation;
        var reader = await store.BeginTransactionAsync(write: false, token);
        var waiting = PausedAsync(store);
        var rewriting = engine.RewriteAsync(database, 0, token).AsTask();
        await waiting.WaitAsync(token);
        var cached = system ? (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token)
            : (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database!, token))!;
        await using (var other = await cached.BeginTransactionAsync(write: false, token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token))
        {
            await Assert.That(await other.GetIncarnationAsync(token)).IsEqualTo(before);
        }

        await PutAsync(cached, StorageFixture.Id("queued"), """{"name":"queued"}""", token).WaitAsync(TimeSpan.FromSeconds(5), token);
        await Assert.That(rewriting.IsCompleted).IsFalse();
        await Assert.That(await reader.GetAsync(Probe, StorageFixture.Id("kept"), token)).IsNotNull();
        await reader.DisposeAsync();

        var rewrite = await rewriting;
        await Assert.That(rewrite.SourceIncarnation).IsEqualTo(before);
        await using var read = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetIncarnationAsync(token)).IsEqualTo(rewrite.TargetIncarnation);
        await Assert.That(await read.GetAsync(Probe, StorageFixture.Id("queued"), token)).IsNotNull();
    }

    [Test]
    public async Task A_rewrite_that_does_not_drain_is_refused_and_leaves_the_old_file_serving()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var (database, store) = await CreateAsync(engine, token);
        await PutAsync(store, StorageFixture.Id("kept"), """{"name":"kept"}""", token);
        var before = store.Incarnation;
        var reader = await store.BeginTransactionAsync(write: false, token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var paused = PausedAsync(store);
        var rewriting = engine.RewriteAsync(database, 0, cancel.Token).AsTask();
        await paused.WaitAsync(token);
        await cancel.CancelAsync();
        await Assert.That(async () => await rewriting).Throws<RewriteRefusedException>();
        await reader.DisposeAsync();

        await Assert.That(store.Incarnation).IsEqualTo(before);
        await PutAsync(store, StorageFixture.Id("after"), """{"name":"after"}""", token);
        await Assert.That(Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, "*.rewriting*")).IsEmpty();
        await Assert.That(async () => await engine.RewriteAsync(database, long.MaxValue, token)).Throws<RewriteRefusedException>();
        await Assert.That(store.Incarnation).IsEqualTo(before);
        reader = await store.BeginTransactionAsync(write: false, token);
        paused = PausedAsync(store);
        rewriting = engine.RewriteAsync(database, 0, token).AsTask();
        try
        {
            await paused.WaitAsync(token);
            await using var housekeeping = (TursoStorageTransaction)await store.BeginTransactionAsync(write: true, token);
            await housekeeping.SuspendCaptureAsync(token);
            await housekeeping.PutAsync(Probe, StorageFixture.Id("uncaptured"), "{}"u8.ToArray(), token);
            await housekeeping.CommitAsync(token);
        }
        finally
        {
            await reader.DisposeAsync();
        }
        await Assert.That(async () => await rewriting).Throws<RewriteRefusedException>();
        await Assert.That(store.Incarnation).IsEqualTo(before);
        await Assert.That(Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, "*.rewriting*")).IsEmpty();
    }

    [Test]
    public async Task A_backup_tap_ends_and_a_failed_native_close_refuses_the_rewrite()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var (database, store) = await CreateAsync(engine, token);
        await PutAsync(store, StorageFixture.Id("kept"), """{"name":"kept"}""", token);
        var tap = await engine.OpenTapAsync(database, token) ?? throw new InvalidOperationException("tap is absent");
        var paused = PausedAsync(store);
        var rewriting = engine.RewriteAsync(database, 0, token).AsTask();
        await paused.WaitAsync(token);
        await Assert.That(rewriting.IsCompleted).IsFalse();
        await Assert.That(tap.Ended).IsTrue();
        await Assert.That(async () => await tap.ReadAsync(Stream.Null, token)).Throws<ObjectDisposedException>();
        await tap.DisposeAsync();
        _ = await rewriting;

        engine.Pool.RecordFailedClose(store);
        var before = store.Incarnation;
        await Assert.That(async () => await engine.RewriteAsync(database, 0, token)).Throws<RewriteRefusedException>();
        await Assert.That(store.Incarnation).IsEqualTo(before);
        await PutAsync(store, StorageFixture.Id("after"), """{"name":"after"}""", token);
    }

    [Test]
    [Arguments("rewrite.old_log_removed")]
    [Arguments("rewrite.replacing")]
    [Arguments("rewrite.replaced")]
    [Arguments("final-sync")]
    [Arguments("final-sync-once")]
    public async Task A_failure_during_the_file_transition_leaves_one_complete_lineage_after_restart(string fault)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var refused = fault is "rewrite.old_log_removed" or "rewrite.replacing";
        var uncertain = !refused;
        var armed = 0;
        var directory = Directory.CreateTempSubdirectory("terrabase-rewrite-").FullName;
        var server = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory, services: services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((reached, _) =>
            {
                if (reached == fault && Interlocked.Exchange(ref armed, 0) == 1)
                {
                    throw new IOException("injected crash");
                }
            }));
        TenantContext database;
        Ulid expected;
        string path;
        try
        {
            var engine = server.Services.GetRequiredService<TursoStorageEngine>();
            (database, var store) = await CreateAsync(engine, token);
            path = store.FilePath;
            await PutAsync(store, StorageFixture.Id("kept"), """{"name":"kept"}""", token);
            var before = store.Incarnation;
            var syncs = 0;
            var sync = engine.DirectorySync;
            engine.DirectorySync = directory =>
            {
                var call = Interlocked.Increment(ref syncs);
                if (fault.StartsWith("final-sync", StringComparison.Ordinal) && (call == 2 || (call == 3 && fault == "final-sync")))
                {
                    throw new IOException("injected directory sync failure");
                }

                sync(directory);
            };
            Volatile.Write(ref armed, 1);
            if (refused)
            {
                await Assert.That(async () => await engine.RewriteAsync(database, 0, token)).Throws<RewriteRefusedException>();
                await Assert.That(store.Incarnation).IsEqualTo(before);
            }
            else
            {
                await Assert.That(async () => await engine.RewriteAsync(database, 0, token)).Throws<RewriteUncertainException>();
                await Assert.That(store.Incarnation).IsNotEqualTo(before);
                await Assert.That(engine.Ledger.FailedClosed).IsNotNull();
                await Assert.That(await engine.IsReadyAsync(token)).IsFalse();
                await Assert.That(async () => await PutAsync(store, StorageFixture.Id("after"), """{"name":"after"}""", token))
                    .Throws<StorageCopyFaultedException>();
            }

            if (fault.StartsWith("final-sync", StringComparison.Ordinal))
            {
                await Assert.That(syncs).IsEqualTo(2);
            }

            expected = store.Incarnation;
            if (!uncertain)
            {
                await PutAsync(store, StorageFixture.Id("after"), """{"name":"after"}""", token);
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, $".{database.DatabaseId}.{EntityIds.New()}.rewriting"), "scratch");
        File.WriteAllText(Path.Combine(directory, $"._system.{EntityIds.New()}.rewriting-wal"), "scratch");
        var reopened = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory);
        try
        {
            await Assert.That(Directory.EnumerateFiles(directory, "*.rewriting*", SearchOption.AllDirectories)).IsEmpty();
            var store = (TursoDatabaseStore)(await reopened.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, token))!;
            await Assert.That(store.Incarnation).IsEqualTo(expected);
            await using (var read = await store.BeginTransactionAsync(write: false, token))
            {
                await Assert.That(await read.GetAsync(Probe, StorageFixture.Id("kept"), token)).IsNotNull();
                await Assert.That(await read.GetAsync(Probe, StorageFixture.Id("after"), token) is null).IsEqualTo(uncertain);
            }

            await PutAsync(store, StorageFixture.Id("restarted"), """{"name":"restarted"}""", token);
        }
        finally
        {
            await reopened.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(TenantContext Database, TursoDatabaseStore Store)> CreateAsync(TursoStorageEngine engine, CancellationToken token)
    {
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await engine.CreateDatabaseAsync(database, token);
        return (database, (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, token))!);
    }

    private static async Task PutAsync(TursoDatabaseStore store, string id, string json, CancellationToken token)
    {
        await using var write = await store.BeginTransactionAsync(write: true, token);
        _ = await write.PutAsync(Probe, id, Encoding.UTF8.GetBytes(json), token);
        await write.CommitAsync(token);
    }

    private static string Payload(string marker) => $$"""{"secret":"{{string.Concat(Enumerable.Repeat(marker, 600))}}"}""";

    private static Task PausedAsync(TursoDatabaseStore store)
    {
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.RewriteWaiting = () => paused.TrySetResult();
        return paused.Task;
    }

    private static IEnumerable<string> SystemFiles(string directory) =>
        Directory.EnumerateFiles(directory, "_system.db*").Concat(Directory.EnumerateFiles(directory, "._system.*"));

    private static bool Holds(IEnumerable<string> files, string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        return files.Any(file => File.ReadAllBytes(file).AsSpan().IndexOf(needle) >= 0);
    }

    private static bool CapturedMarker(string path, string marker)
    {
        using var connection = new Turso.TursoConnection(TestMasterKey.ConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = 'turso_cdc';";
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return false;
        }

        command.CommandText = "SELECT after FROM turso_cdc WHERE after IS NOT NULL;";
        using var rows = command.ExecuteReader();
        var needle = Encoding.UTF8.GetBytes(marker);
        while (rows.Read())
        {
            if (((byte[])rows.GetValue(0)).AsSpan().IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
