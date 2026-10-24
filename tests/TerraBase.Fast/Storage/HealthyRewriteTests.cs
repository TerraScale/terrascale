using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

/// <summary>TS-873: a healthy rewrite drops every physical copy of deleted rows and keeps every logical state.</summary>
public sealed class HealthyRewriteTests
{
    private const string Probe = "rewrite_probe";

    [Test]
    public async Task A_system_rewrite_removes_deleted_payloads_from_every_file_and_keeps_every_logical_state()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("terrabase-rewrite-").FullName;
        var server = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory);
        HealthyRewrite rewrite;
        var marker = "erased-" + EntityIds.New();
        try
        {
            var engine = server.Services.GetRequiredService<TursoStorageEngine>();
            var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
            await PutAsync(store, StorageFixture.Id("gone"), Payload(marker), token);
            await PutAsync(store, StorageFixture.Id("kept"), """{"name":"survivor"}""", token);
            CommitResult deletion;
            await using (var delete = await store.BeginTransactionAsync(write: true, token))
            {
                _ = await delete.DeleteAsync(Probe, StorageFixture.Id("gone"), token);
                deletion = await delete.CommitAsync(token);
            }

            await StorageFixture.ExecuteWhileWriterHeldAsync(server, null, "UPDATE _tb_gc_floor SET stamp = 123456;");
            await StorageFixture.ExecuteWhileWriterHeldAsync(server, null,
                "INSERT INTO _tb_peers (node, applied, acked, stable, seen) VALUES (7, 111, 555, 99, 3);");
            await Assert.That(Holds(SystemFiles(directory), marker)).IsTrue();

            rewrite = await engine.RewriteAsync(null, deletion.Offset, token);

            await Assert.That(rewrite.Offset).IsGreaterThanOrEqualTo(deletion.Offset);
            await Assert.That(rewrite.TargetIncarnation).IsNotEqualTo(rewrite.SourceIncarnation);
            // The cached reference serves the new file.
            await Assert.That(ReferenceEquals(await engine.OpenSystemDatabaseAsync(token), store)).IsTrue();
            await Assert.That(store.Incarnation).IsEqualTo(rewrite.TargetIncarnation);
            await Assert.That(Holds(SystemFiles(directory), marker)).IsFalse();
            await AssertRetainedAsync(store, token);
            await PutAsync(store, StorageFixture.Id("after"), """{"name":"later"}""", token);
        }
        finally
        {
            await server.DisposeAsync();
        }

        await Assert.That(Holds(SystemFiles(directory), marker)).IsFalse();
        var reopened = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory);
        try
        {
            var store = (TursoDatabaseStore)await reopened.Services.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(token);
            await Assert.That(store.Incarnation).IsEqualTo(rewrite.TargetIncarnation);
            await AssertRetainedAsync(store, token);
        }
        finally
        {
            await reopened.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }

        static async Task AssertRetainedAsync(TursoDatabaseStore store, CancellationToken token)
        {
            await using var read = await store.BeginTransactionAsync(write: false, token);
            await Assert.That(await read.GetIncarnationAsync(token)).IsEqualTo(store.Incarnation);
            await Assert.That(await read.GetAsync(Probe, StorageFixture.Id("gone"), token)).IsNull();
            await Assert.That(await read.GetAsync(Probe, StorageFixture.Id("kept"), token)).IsNotNull();
            await Assert.That(Scalar(store.FilePath, "SELECT stamp FROM _tb_gc_floor;")).IsEqualTo("123456");
            // Applied and stable progress stays; the acknowledged change id named the old file's change log.
            await Assert.That(Scalar(store.FilePath, "SELECT applied || ',' || acked || ',' || stable || ',' || seen FROM _tb_peers WHERE node = 7;"))
                .IsEqualTo("111,0,99,3");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_rewrite_holds_cached_readers_and_writers_until_the_new_file_serves_them(bool system)
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
        var reader = await store.BeginTransactionAsync(write: false, token);
        var waiting = PausedAsync(store);
        var rewriting = engine.RewriteAsync(database, 0, token).AsTask();
        await waiting.WaitAsync(token);
        // Through the cached fast path: both wait rather than fail or reach the old file.
        var cached = system ? (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token)
            : (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database!, token))!;
        var waitingReader = cached.BeginTransactionAsync(write: false, token).AsTask();
        var queued = PutAsync(cached, StorageFixture.Id("queued"), """{"name":"queued"}""", token);
        await Assert.That(rewriting.IsCompleted).IsFalse();
        await Assert.That(waitingReader.IsCompleted).IsFalse();
        await Assert.That(queued.IsCompleted).IsFalse();
        await Assert.That(await reader.GetAsync(Probe, StorageFixture.Id("kept"), token)).IsNotNull();
        await reader.DisposeAsync();

        var rewrite = await rewriting;
        await queued;
        await using (var resumed = await waitingReader)
        {
            await Assert.That(await resumed.GetIncarnationAsync(token)).IsEqualTo(rewrite.TargetIncarnation);
        }

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
        // A cut that does not include the requested commit is refused too.
        await Assert.That(async () => await engine.RewriteAsync(database, long.MaxValue, token)).Throws<RewriteRefusedException>();
        await Assert.That(store.Incarnation).IsEqualTo(before);
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
        // Its owner reads next, learns the file is being replaced, and disposes it (ContinuousBackup.Failed).
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
        // Before the rename the old file serves; after it, any failed sync (even one a retry would pass) fails storage closed.
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
            // The rewrite's second directory sync is the one after the rename; a third would be a retry.
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
                // No write is acknowledged on a file a power loss could still take back.
                await Assert.That(async () => await PutAsync(store, StorageFixture.Id("after"), """{"name":"after"}""", token))
                    .Throws<ReplicaCopyFaultedException>();
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

        // A crash's private copy is recognized and removed at start.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, $".{database.DatabaseId}.{EntityIds.New()}.rewriting"), "scratch");
        File.WriteAllText(Path.Combine(directory, $"._system.{EntityIds.New()}.rewriting-wal"), "scratch");
        var reopened = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory);
        try
        {
            await Assert.That(Directory.EnumerateFiles(directory, "*.rewriting*", SearchOption.AllDirectories)).IsEmpty();
            var store = (TursoDatabaseStore)(await reopened.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, token))!;
            // Without a real power loss the renamed file is what is durable here.
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

    // Spread over many pages, so freed pages, the log and change capture all held it before the rewrite.
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

    private static string? Scalar(string path, string sql)
    {
        using var connection = new Turso.TursoConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
