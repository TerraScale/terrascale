using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using TerraBase.Engine.Turso;
using TerraBase.Engine;
using TerraBase.Fast.Documents;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

public sealed class HealthyRewriteBoundsTests
{
    [Test]
    public async Task Updates_deletes_and_collection_create_drop_during_copy_are_verified_and_kept()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartIsolatedAsync();
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var database = await api.TenantOfAsync();
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, token))!;
        var kept = EntityIds.New().ToString();
        var gone = EntityIds.New().ToString();
        await WriteAsync(store, async write =>
        {
            await write.PutAsync("items", kept, "{}"u8.ToArray(), token);
            await write.PutAsync("items", gone, "{}"u8.ToArray(), token);
            await write.CreateCollectionAsync(new CollectionInfo("old_collection", 1), token);
        }, token);
        using var release = new ManualResetEventSlim();
        var pinned = Pause(store, release);
        var rewrite = engine.RewriteAsync(database, 0, token).AsTask();
        try
        {
            await pinned.WaitAsync(TimeSpan.FromSeconds(30), token);
            await WriteAsync(store, async write =>
            {
                await write.PutAsync("items", kept, "{\"n\":2}"u8.ToArray(), token);
                await write.DeleteAsync("items", gone, token);
                await write.DropCollectionAsync("old_collection", token);
                await write.CreateCollectionAsync(new CollectionInfo("new_collection", 1), token);
            }, token);
        }
        finally
        {
            release.Set();
        }
        _ = await rewrite;
        await using var read = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(Encoding.UTF8.GetString((await read.GetAsync("items", kept, token))!.Json.Span)).IsEqualTo("{\"n\":2}");
        await Assert.That(await read.GetAsync("items", gone, token)).IsNull();
        await Assert.That(await read.GetCollectionAsync("old_collection", token)).IsNull();
        await Assert.That(await read.GetCollectionAsync("new_collection", token)).IsNotNull();
    }

    [Test]
    public async Task A_misclassified_uncaptured_write_is_caught_by_the_replay_digest_without_ending_a_backup()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        var before = store.Incarnation;
        await using var tap = (await engine.OpenTapAsync(null, token))!;
        using var release = new ManualResetEventSlim();
        var pinned = Pause(store, release);
        var rewrite = engine.RewriteAsync(null, 0, token).AsTask();
        try
        {
            await pinned.WaitAsync(TimeSpan.FromSeconds(30), token);
            await WriteAsync(store, async write =>
            {
                await write.SuspendCaptureAsync(token, rewriteRefreshOnly: true);
                await write.PutAsync("items", EntityIds.New().ToString(), "{}"u8.ToArray(), token);
            }, token);
        }
        finally
        {
            release.Set();
        }
        var error = await Assert.ThrowsAsync<RewriteRefusedException>(async () => await rewrite);
        await Assert.That(error!.Message).Contains("same logical state");
        await Assert.That(tap.Ended).IsFalse();
        await CleanAsync(store, before, token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_pinned_copy_aborts_on_total_deadline_or_sustained_io_pressure(bool pressure)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await TerraBaseTestServer.StartAsync(settings => settings["storage-threads"] = "2",
            services: services => services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock));
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        var before = store.Incarnation;
        var lineage = store.Lineage;
        using var holds = new WriterHoldTests.WriterHolds(server);
        using var release = new ManualResetEventSlim();
        using var busy = new ManualResetEventSlim();
        var pinned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.RewriteSnapshotPinned = copyToken =>
        {
            using var cancelled = copyToken.Register(() => stopped.TrySetResult());
            pinned.TrySetResult();
            release.Wait(copyToken);
        };
        var started = Stopwatch.GetTimestamp();
        var rewrite = engine.RewriteAsync(null, 0, token).AsTask();
        RewriteRefusedException? error = null;
        List<Task> queued = [];
        try
        {
            await pinned.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            if (pressure)
            {
                for (var i = 0; i < 4; i++)
                {
                    engine.Io.Admit();
                    queued.Add(engine.Io.RunAsync(busy.Wait, token));
                }
                await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(engine.Io.Queued >= 2));
            }
            // Keep the domain clock frozen: safety deadlines and pressure samples must follow real elapsed time.
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            busy.Set(); // Cancellation releases the pin; let the saturated pool run the required cleanup.
            error = await Assert.ThrowsAsync<RewriteRefusedException>(async () =>
                await rewrite.WaitAsync(TimeSpan.FromSeconds(30), token));
        }
        finally
        {
            release.Set();
            busy.Set();
            await Task.WhenAll(queued);
            foreach (var _ in queued)
            {
                engine.Io.Release();
            }
        }
        await Assert.That(error!.Reason).IsEqualTo(pressure ? RewriteRefusal.Pressure : RewriteRefusal.Deadline);
        await Assert.That(Stopwatch.GetElapsedTime(started)).IsLessThan(TimeSpan.FromSeconds(30));
        await Assert.That(store.Lineage).IsEqualTo(lineage);
        await Assert.That(holds.Measured("a healthy rewrite")).IsEmpty();
        await CleanAsync(store, before, token);
        store.RewriteSnapshotPinned = null;
        _ = await engine.RewriteAsync(null, 0, token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Both_operations_waits_refuse_in_real_time_with_a_frozen_domain_clock(bool secondWait)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await TerraBaseTestServer.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock));
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await engine.CreateDatabaseAsync(database, token);
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, token))!;
        var before = store.Incarnation;
        var lineage = store.Lineage;
        var checkpointed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        TursoDatabaseStore.CopyOperation? operation = null;
        if (secondWait)
        {
            store.RewriteStepMeasured = (step, _) =>
            {
                if (step == "source_pre_fsync")
                {
                    checkpointed.TrySetResult();
                    release.Wait(token);
                }
            };
        }
        else
        {
            operation = await store.BeginOperationAsync(token);
        }
        var started = Stopwatch.GetTimestamp();
        var rewrite = engine.RewriteAsync(database, 0, token).AsTask();
        try
        {
            if (secondWait)
            {
                await checkpointed.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                operation = await store.BeginOperationAsync(token);
                release.Set();
            }
            var error = await Assert.ThrowsAsync<RewriteRefusedException>(async () =>
                await rewrite.WaitAsync(TimeSpan.FromSeconds(20), token));
            await Assert.That(error!.Message).Contains("operations did not end");
            await Assert.That(error!.Reason).IsEqualTo(RewriteRefusal.Deadline);
            await Assert.That(Stopwatch.GetElapsedTime(started)).IsLessThan(TimeSpan.FromSeconds(20));
            await Assert.That(store.Lineage).IsEqualTo(lineage + (secondWait ? 1 : 0));
            await CleanAsync(store, before, token);
        }
        finally
        {
            release.Set();
            if (operation is not null)
            {
                operation.Complete();
                await operation.DisposeAsync();
            }
            store.RewriteStepMeasured = null;
        }
        _ = await engine.RewriteAsync(database, 0, token);
    }

    [Test]
    public async Task The_lifecycle_wait_refuses_in_real_time_with_a_frozen_domain_clock()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services
            .AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock)
            .AddSingleton<TerraBase.Hosting.DatabaseLifecycleCheckpoint>((step, creating) =>
            {
                if (step == "create.published" && creating.DatabaseId == database.DatabaseId)
                {
                    locked.TrySetResult();
                    release.Wait(token);
                }
            }));
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        var before = store.Incarnation;
        var lineage = store.Lineage;
        var create = engine.CreateDatabaseAsync(database, token).AsTask();
        try
        {
            await locked.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            var error = await Assert.ThrowsAsync<RewriteRefusedException>(async () =>
                await engine.RewriteAsync(null, 0, token).AsTask().WaitAsync(TimeSpan.FromSeconds(25), token));
            await Assert.That(error!.Reason).IsEqualTo(RewriteRefusal.Deadline);
            await Assert.That(store.Lineage).IsEqualTo(lineage);
            await CleanAsync(store, before, token);
        }
        finally
        {
            release.Set();
            _ = await create;
        }
        _ = await engine.RewriteAsync(null, 0, token);
    }

    [Test]
    public async Task Eight_nonconverging_rounds_refuse_without_restarting_a_backup_generation()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        // One captured SQL batch per round avoids spending the total deadline on thousands of test API calls.
        await WriteAsync(store, async write => _ = await write.LedgerAsync(command =>
        {
            using var create = command("CREATE TABLE rewrite_round_probe (id INTEGER PRIMARY KEY, value INTEGER);");
            return create.ExecuteNonQuery();
        }, token), token);
        var before = store.Incarnation;
        var lineage = store.Lineage;
        var rounds = Channel.CreateBounded<int>(1);
        using var release = new ManualResetEventSlim();
        store.RewriteRoundCompleted = round =>
        {
            rounds.Writer.TryWrite(round);
            release.Wait(token);
            release.Reset();
        };
        var rewrite = engine.RewriteAsync(null, 0, token).AsTask();
        for (var round = 0; round < 8; round++)
        {
            await Assert.That(await rounds.Reader.ReadAsync(token)).IsEqualTo(round);
            try
            {
                await WriteAsync(store, async write => _ = await write.LedgerAsync(command =>
                {
                    using var insert = command("""
                        WITH RECURSIVE rows(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM rows WHERE n < 1025)
                        INSERT INTO rewrite_round_probe (value) SELECT n FROM rows;
                        """);
                    return insert.ExecuteNonQuery();
                }, token), token);
            }
            finally
            {
                release.Set();
            }
        }
        var error = await Assert.ThrowsAsync<RewriteRefusedException>(async () => await rewrite);
        await Assert.That(error!.Reason).IsEqualTo(RewriteRefusal.Budget);
        await Assert.That(error.Message).Contains("round budget");
        await Assert.That(store.Lineage).IsEqualTo(lineage);
        store.RewriteRoundCompleted = null;
        await CleanAsync(store, before, token);
        _ = await engine.RewriteAsync(null, 0, token);
    }

    [Test]
    [Arguments("rows")]
    [Arguments("bytes")]
    [Arguments("schema")]
    [Arguments("deadline")]
    public async Task The_final_delta_and_final_deadline_refuse_and_release_the_writer(string mode)
    {
        var deadline = mode == "deadline";
        var token = TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await TerraBaseTestServer.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock));
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        var before = store.Incarnation;
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (deadline)
        {
            store.RewriteFinalStarting = finalToken => release.Wait(finalToken);
        }
        else
        {
            store.RewriteBeforeWriter = () =>
            {
                ready.TrySetResult();
                release.Wait(token);
            };
        }
        var rewrite = engine.RewriteAsync(null, 0, token).AsTask();
        if (!deadline)
        {
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                if (mode == "rows")
                {
                    await WriteManyAsync(store, token);
                }
                else if (mode == "bytes")
                {
                    var payload = Encoding.UTF8.GetBytes("{\"body\":\"" + new string('x', 512 * 1024) + "\"}");
                    // Each commit respects the 3 MiB transaction limit; together they exceed the final 4 MiB delta.
                    for (var i = 0; i < 9; i++)
                    {
                        await WriteAsync(store, async write =>
                            await write.PutAsync("items", EntityIds.New().ToString(), payload, token), token);
                    }
                }
                else
                {
                    await WriteAsync(store, async write =>
                    {
                        // SYSTEM collections share a table; an actual captured schema statement tests final DDL refusal.
                        await write.LedgerAsync(command =>
                        {
                            using var ddl = command("CREATE TABLE final_schema_probe (id INTEGER PRIMARY KEY, body TEXT);");
                            return ddl.ExecuteNonQuery();
                        }, token);
                    }, token);
                }
            }
            finally
            {
                release.Set();
            }
        }
        var error = await Assert.ThrowsAsync<RewriteRefusedException>(async () => await rewrite);
        await Assert.That(error!.Reason).IsEqualTo(RewriteRefusal.Budget);
        await Assert.That(error.Message).Contains(deadline ? "time budget" : "budget");
        store.RewriteBeforeWriter = null;
        store.RewriteFinalStarting = null;
        await CleanAsync(store, before, token);
        _ = await engine.RewriteAsync(null, 0, token);
    }

    [Test]
    public async Task Backup_reads_keep_the_rewrite_anchor_in_the_bounded_retention_path()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        await WriteAsync(store, async write => await write.PutAsync("items", EntityIds.New().ToString(), "{}"u8.ToArray(), token), token);
        var tap = (await engine.OpenTapAsync(null, token))!;
        using var release = new ManualResetEventSlim();
        var pinned = Pause(store, release);
        var rewrite = engine.RewriteAsync(null, 0, token).AsTask();
        try
        {
            await pinned.WaitAsync(TimeSpan.FromSeconds(30), token);
            var anchor = store.RewriteAnchor;
            await Assert.That(anchor).IsGreaterThan(0);
            for (var i = 0; i < 2; i++)
            {
                await WriteAsync(store, async write => await write.PutAsync("items", EntityIds.New().ToString(), "{}"u8.ToArray(), token), token);
                using var segment = new MemoryStream();
                _ = await tap.ReadAsync(segment, token);
            }
            await using var read = (TursoStorageTransaction)await store.BeginTransactionAsync(write: false, token);
            var present = await read.LedgerAsync(command =>
            {
                using var query = command("SELECT 1 FROM turso_cdc WHERE change_id = $anchor;");
                query.Parameters.AddWithValue("$anchor", anchor);
                return query.ExecuteScalar();
            }, token);
            await Assert.That(present).IsNotNull();
        }
        finally
        {
            await tap.DisposeAsync();
            release.Set();
        }
        _ = await rewrite;
    }

    [Test]
    public async Task A_source_checkpoint_waits_until_the_pinned_backup_snapshot_has_closed()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        await using var tap = (TursoChangeTap)(await engine.OpenTapAsync(null, token))!;
        var pinned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tap.AfterSnapshotPinned = async _ =>
        {
            pinned.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var snapshot = tap.SnapshotAsync(Stream.Null, Path.Combine(server.DataDirectory, "pinned-snapshot.db"), token).AsTask();
        await pinned.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var checkpointed = 0;
        store.RewriteStepMeasured = (step, _) =>
        {
            if (step == "source_pre_checkpoint")
            {
                Interlocked.Increment(ref checkpointed);
            }
        };
        store.RewriteWaiting = () => waiting.TrySetResult();
        var rewrite = engine.RewriteAsync(null, 0, token).AsTask();
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            await Assert.That(tap.Ended).IsTrue();
            await Assert.That(Volatile.Read(ref checkpointed)).IsEqualTo(0);
        }
        finally
        {
            release.TrySetResult();
            _ = await snapshot;
            await tap.DisposeAsync();
        }
        _ = await rewrite.WaitAsync(TimeSpan.FromSeconds(10), token);
        await Assert.That(Volatile.Read(ref checkpointed)).IsEqualTo(1);
    }

    [Test]
    public async Task A_rewrite_uses_one_admission_even_when_capacity_is_one()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync(settings => settings["storage-queue"] = "1");
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = await engine.OpenSystemDatabaseAsync(token);
        var before = store.Incarnation;
        var rewrite = await engine.RewriteAsync(null, 0, token);
        await Assert.That(rewrite.SourceIncarnation).IsEqualTo(before);
        await Assert.That(rewrite.TargetIncarnation).IsEqualTo(store.Incarnation);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cancellation_or_shutdown_mid_copy_cleans_scratch_and_releases_admission(bool shutdown)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var release = new ManualResetEventSlim();
        var pinned = Pause(store, release);
        var rewrite = engine.RewriteAsync(null, 0, cancel.Token).AsTask();
        await pinned.WaitAsync(TimeSpan.FromSeconds(30), token);
        var stop = shutdown ? engine.DisposeAsync().AsTask() : Task.CompletedTask;
        if (!shutdown)
        {
            await cancel.CancelAsync();
        }
        release.Set();
        await Assert.That(async () => await rewrite).Throws<OperationCanceledException>();
        await stop.WaitAsync(TimeSpan.FromSeconds(10), token);
        await Assert.That(store.Rewriting).IsFalse();
        await Assert.That(store.RewriteAnchor).IsEqualTo(-1);
        await Assert.That(Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, "*.rewriting*")).IsEmpty();
        // The live host's background passes may briefly own an admission after caller cancellation.
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(engine.Io.Admitted == 0));
    }

    private static Task Pause(TursoDatabaseStore store, ManualResetEventSlim release)
    {
        var pinned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.RewriteSnapshotPinned = copyToken =>
        {
            pinned.TrySetResult();
            release.Wait(copyToken);
        };
        return pinned.Task;
    }

    private static async Task WriteManyAsync(TursoDatabaseStore store, CancellationToken token) =>
        await WriteAsync(store, async write =>
        {
            for (var i = 0; i < 600; i++)
            {
                await write.PutAsync("items", EntityIds.New().ToString(), "{}"u8.ToArray(), token);
            }
        }, token);

    private static async Task WriteAsync(TursoDatabaseStore store, Func<TursoStorageTransaction, Task> change, CancellationToken token)
    {
        await using var write = (TursoStorageTransaction)await store.BeginTransactionAsync(write: true, token);
        await change(write);
        await write.CommitAsync(token);
    }

    private static async Task CleanAsync(TursoDatabaseStore store, Ulid before, CancellationToken token)
    {
        await Assert.That(store.Incarnation).IsEqualTo(before);
        await Assert.That(store.Rewriting).IsFalse();
        await Assert.That(store.Quiescing).IsFalse();
        await Assert.That(store.RewriteAnchor).IsEqualTo(-1);
        await Assert.That(Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, "*.rewriting*")).IsEmpty();
        await using var read = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetIncarnationAsync(token)).IsEqualTo(before);
    }
}
