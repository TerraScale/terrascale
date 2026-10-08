using System.Diagnostics.Metrics;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

public sealed class BaselineInitializationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Creation_pins_its_own_incarnation_before_a_delete_queued_at_publication(bool copy)
    {
        var fake = new Faker();
        var target = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby,
            EntityIds.New().ToString(), EntityIds.New().ToString());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        SqliteStorageEngine? engine = null;
        SqliteDatabaseStore? initialized = null;
        Task<bool>? deletion = null;
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<TerraBaseOptions>();
            engine = new SqliteStorageEngine(options.DataDirectory, options.StorageThreads, options.StorageQueue, options.MemoryBudget,
                provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName),
                provider.GetRequiredService<ILogger<SqliteStorageEngine>>(), options.ShutdownGrace, readinessCacheTtl: options.ReadinessCacheTtl,
                checkpoint: (step, database) =>
                {
                    if (step == "create.published" && database == target && deletion is null)
                    {
                        // Queue the deletion while publication still owns the lifecycle lock.
                        deletion = engine!.DeleteDatabaseAsync(target, deadline.Token).AsTask();
                    }
                }, afterBootstrap: store =>
                {
                    if (store.Tenant == target)
                    {
                        initialized = store;
                    }
                });
            return engine;
        }));
        try
        {
            var source = target with { DatabaseId = EntityIds.New().ToString() };
            if (copy)
            {
                await engine!.CreateDatabaseAsync(source, deadline.Token);
            }

            var created = copy
                ? await engine!.CopyDatabaseAsync(source, target, deadline.Token)
                : await engine!.CreateDatabaseAsync(target, deadline.Token);
            await Assert.That(created).IsTrue();
            await Assert.That(deletion is not null).IsTrue();
            await Assert.That(await deletion!).IsTrue();
            await Assert.That(initialized).IsNotNull();
            var original = initialized!;
            await Assert.That(original.IsClosed).IsTrue();
            await Assert.That(await engine.OpenDatabaseAsync(target, deadline.Token)).IsNull();
            await Assert.That(await engine.CreateDatabaseAsync(target, deadline.Token)).IsTrue();
            var replacement = (await engine.OpenDatabaseAsync(target, deadline.Token))!;
            await Assert.That(replacement.Incarnation).IsNotEqualTo(original.Incarnation);
            using var health = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), deadline.Token);
            health.EnsureSuccessStatusCode();
        }
        finally
        {
            if (deletion is not null)
            {
                await deletion;
            }
        }
    }

    [Test]
    [Arguments("open")]
    [Arguments("copy")]
    [Arguments("delete")]
    [Arguments("shutdown")]
    [Arguments("shutdown-zero")]
    [Arguments("cancel-copy")]
    [Arguments("intent-copy")]
    [Arguments("cancel-intent-copy")]
    public async Task A_paused_baseline_is_isolated_and_its_store_stays_pinned_until_initialization_ends(string action)
    {
        var fake = new Faker();
        static TenantContext Database()
        {
            return new(EntityIds.New().ToString(), PlanTier.Hobby,
                EntityIds.New().ToString(), EntityIds.New().ToString());
        }
        var target = Database();
        var unrelated = Database();
        var legacy = Database();
        var pause = new PausedBaseline();
        SqliteStorageEngine? engine = null;
        SqliteDatabaseStore? initializingStore = null;
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            if (action == "shutdown-zero")
            {
                settings["shutdown-grace-seconds"] = "0";
            }
        }, services => services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<TerraBaseOptions>();
            engine = new SqliteStorageEngine(options.DataDirectory, options.StorageThreads, options.StorageQueue, options.MemoryBudget,
                provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName),
                provider.GetRequiredService<ILogger<SqliteStorageEngine>>(), options.ShutdownGrace, readinessCacheTtl: options.ReadinessCacheTtl,
                checkpoint: provider.GetRequiredService<DatabaseLifecycleCheckpoint>(), afterBootstrap: store =>
                {
                    if (store.Tenant != target)
                    {
                        return;
                    }

                    initializingStore = store;
                    var session = engine!.Pool.Rent(store) ?? throw new InvalidOperationException("migration left no idle session");
                    try
                    {
                        session.Connection.CreateFunction("hold_baseline", () => { pause.Hold(); return 0; });
                        using var trigger = session.Connection.CreateCommand();
                        trigger.CommandText = "CREATE TEMP TRIGGER hold_baseline AFTER INSERT ON changes BEGIN SELECT hold_baseline(); END;";
                        trigger.ExecuteNonQuery();
                    }
                    finally
                    {
                        store.Return(session);
                    }
                });
            return engine;
        }));
        var value = fake.Lorem.Word();
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { value });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var withIntent = action is "intent-copy" or "cancel-intent-copy";
        var copy = action is "copy" or "cancel-copy" || withIntent;
        var path = File(server, target);
        TenantContext? source = null;
        if (copy)
        {
            source = target with { DatabaseId = EntityIds.New().ToString() };
            await engine!.CreateDatabaseAsync(source, deadline.Token);
            var sourceStore = (await engine.OpenDatabaseAsync(source, deadline.Token))!;
            await using var write = await sourceStore.BeginTransactionAsync(write: true, deadline.Token);
            await write.CreateCollectionAsync(new CollectionInfo("items", 0), deadline.Token);
            await write.PutAsync("items", "a", body, deadline.Token);
            await write.CommitAsync(deadline.Token);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await DocumentFiles.WriteAsync(path, ("items", "a", System.Text.Encoding.UTF8.GetString(body)));
        }

        var legacyPath = File(server, legacy);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        await DocumentFiles.WriteAsync(legacyPath, ("items", "b", "{}"));
        Task initialization = withIntent
            ? engine!.CreateDatabaseWithIntentAsync(target, source, static _ => ValueTask.CompletedTask, request.Token).AsTask()
            : copy
                ? engine!.CopyDatabaseAsync(source!, target, request.Token).AsTask()
                : engine!.OpenDatabaseAsync(target, request.Token).AsTask();
        Task<IDatabaseStore?>? second = null;
        Task? lifecycle = null;
        try
        {
            await pause.Entered.Task.WaitAsync(deadline.Token);
            second = engine!.OpenDatabaseAsync(target, deadline.Token).AsTask();
            await Assert.That(second.IsCompleted).IsFalse();
            await Assert.That(await engine.CreateDatabaseAsync(unrelated, deadline.Token)).IsTrue();
            await Assert.That(await engine.OpenDatabaseAsync(legacy, deadline.Token)).IsNotNull();
            await Assert.That(await engine.DeleteDatabaseAsync(unrelated, deadline.Token)).IsTrue();
            using var health = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), deadline.Token);
            health.EnsureSuccessStatusCode();
            await Assert.That(initialization.IsCompleted).IsFalse();
            if (withIntent)
            {
                await Assert.That(async () => await engine.CreateDatabaseAsync(target, deadline.Token)).Throws<IOException>();
                await Assert.That(async () => await engine.DeleteDatabaseAsync(target, deadline.Token)).Throws<IOException>();
            }

            if (action == "delete")
            {
                lifecycle = engine.DeleteDatabaseAsync(target, deadline.Token).AsTask();
                await Assert.That(lifecycle.IsCompleted).IsFalse();
                await Assert.That(System.IO.File.Exists(path)).IsTrue();
            }
            else if (action is "shutdown" or "shutdown-zero")
            {
                lifecycle = engine.DisposeAsync().AsTask();
                if (action == "shutdown-zero")
                {
                    // The disposer finishes while SQLite is still held, retaining the directory.
                    await lifecycle.WaitAsync(deadline.Token);
                    await engine.DisposeAsync().AsTask().WaitAsync(deadline.Token);
                    await Assert.That(initialization.IsCompleted).IsFalse();
                }
                else
                {
                    await Assert.That(lifecycle.IsCompleted).IsFalse();
                }

                await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(server.DataDirectory)).IsTrue();
            }
            else if (action is "cancel-copy" or "cancel-intent-copy")
            {
                request.Cancel();
                await Assert.That(initialization.IsCompleted).IsFalse();
                await Assert.That(second.IsCompleted).IsFalse();
            }

            pause.Release();
            if (action is "shutdown" or "shutdown-zero" or "cancel-copy" or "cancel-intent-copy")
            {
                await Assert.That(async () => await initialization).Throws<OperationCanceledException>();
                await Assert.That(async () => await second).Throws<OperationCanceledException>();
                if (lifecycle is not null)
                {
                    await lifecycle.WaitAsync(deadline.Token);
                    await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(!TerraBaseTestServer.IsDataDirectoryOwned(server.DataDirectory)));
                }

                await Assert.That(initializingStore!.IsClosed).IsTrue();
                await Assert.That(initializingStore.IdleCount).IsEqualTo(0);
            }
            else
            {
                await initialization.WaitAsync(deadline.Token);
                var store = (await second)!;
                if (lifecycle is not null)
                {
                    await lifecycle.WaitAsync(deadline.Token);
                    await Assert.That(store.IsClosed).IsTrue();
                    await Assert.That(await engine.OpenDatabaseAsync(target, deadline.Token)).IsNull();
                }
                else
                {
                    await using var read = await store.BeginTransactionAsync(write: false, deadline.Token);
                    await Assert.That((await read.GetAsync("items", "a", deadline.Token))!.Json.Span.SequenceEqual(body)).IsTrue();
                    await Assert.That(await read.GetLastChangeSequenceAsync(deadline.Token)).IsEqualTo(1);
                }
            }

            if (action is "cancel-copy" or "cancel-intent-copy")
            {
                var retry = (await engine!.OpenDatabaseAsync(target, deadline.Token))!;
                await using var read = await retry.BeginTransactionAsync(write: false, deadline.Token);
                await Assert.That((await read.GetAsync("items", "a", deadline.Token))!.Json.Span.SequenceEqual(body)).IsTrue();
                await Assert.That(await read.GetLastChangeSequenceAsync(deadline.Token)).IsEqualTo(1);
            }
        }
        finally
        {
            pause.Release();
            await ObserveAsync(initialization);
            if (second is not null)
            {
                await ObserveAsync(second);
            }

            if (lifecycle is not null)
            {
                await lifecycle;
            }

            if (withIntent)
            {
                await engine!.CompleteDatabaseIntentAsync(target, CancellationToken.None);
            }
        }
    }

    private static string File(TerraBaseTestServer server, TenantContext database) =>
        Path.Combine(server.DataDirectory, database.OrganizationId, database.ProjectId!, database.DatabaseId + ".db");

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class PausedBaseline
    {
        private readonly object _gate = new();
        private bool _released;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Hold()
        {
            lock (_gate)
            {
                Entered.TrySetResult();
                while (!_released)
                {
                    if (!Monitor.Wait(_gate, TimeSpan.FromSeconds(30)))
                    {
                        throw new TimeoutException("the test did not release the baseline");
                    }
                }
            }
        }

        public void Release()
        {
            lock (_gate)
            {
                _released = true;
                Monitor.PulseAll(_gate);
            }
        }
    }
}
