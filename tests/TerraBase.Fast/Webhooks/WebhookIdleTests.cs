using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Webhooks;

public sealed class WebhookIdleTests
{
    [Test]
    public async Task Idle_discovery_leaves_the_catalog_unversioned_and_storage_capacity_available()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            settings["storage-threads"] = "1";
            settings["storage-queue"] = "1";
        }, services: services => services.AddSingleton<TimeProvider>(clock));
        var engine = server.Services.GetRequiredService<IStorageEngine>();
        var system = await engine.OpenSystemDatabaseAsync(token);
        await using var read = await system.BeginTransactionAsync(write: false, token);
        var offset = await read.GetOffsetAsync(token);
        await Assert.That(await read.GetAsync("catalog", "version", token)).IsNull();
        for (var tick = 0; tick < 3; tick++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            using var health = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), token);
            await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
            // The held read owns the only slot: even an absence lookup must stay in memory.
            await Assert.That(await engine.MayHaveSystemCollectionStateAsync("webhook_dispatch", token)).IsFalse();
        }

        await Assert.That(await read.GetAsync("catalog", "version", token)).IsNull();
        await Assert.That(await read.GetOffsetAsync(token)).IsEqualTo(offset);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Local_and_replicated_state_invalidate_cached_absence_without_another_storage_read(bool replicated)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync(settings => settings["storage-queue"] = "1");
        using var health = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), token);
        health.EnsureSuccessStatusCode();
        var engine = server.Services.GetRequiredService<IStorageEngine>();
        const string Collection = "presence_probe";
        await Assert.That(await engine.MayHaveSystemCollectionStateAsync(Collection, token)).IsFalse();
        var system = await engine.OpenSystemDatabaseAsync(token);
        using var value = JsonDocument.Parse("""{"value":true}""");
        await using (var write = await system.BeginTransactionAsync(write: true, token))
        {
            if (replicated)
            {
                var at = new EntryVersion(FeedCursor.At(DateTimeOffset.UtcNow), ReplicaId.Of(4518, 9, 1));
                _ = await write.JoinAsync(Collection, EntityIds.New().ToString(), DocumentState.Bottom.Put(at, value.RootElement), at, token);
            }
            else
            {
                _ = await write.PutAsync(Collection, EntityIds.New().ToString(), """{"value":true}"""u8.ToArray(), token);
            }

            _ = await write.CommitAsync(token);
        }

        await using var held = await system.BeginTransactionAsync(write: false, token);
        await Assert.That(await engine.MayHaveSystemCollectionStateAsync(Collection, token)).IsTrue();
        await Assert.That(engine.IoAdmittedCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task First_discovery_waits_for_writes_that_notified_before_the_collection_was_probed(bool replicated)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            settings["storage-threads"] = "1";
            settings["storage-queue"] = "2";
        }, services: services => services.AddSingleton<TimeProvider>(clock));
        using var health = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), token);
        health.EnsureSuccessStatusCode();
        var engine = server.Services.GetRequiredService<SqliteStorageEngine>();
        var system = (SqliteDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        // Keep an idle reader available: an unsynchronized probe would rent it and enqueue an older snapshot.
        await using (var warm = await system.BeginTransactionAsync(write: false, token))
        {
            _ = await warm.GetOffsetAsync(token);
        }

        await using var write = await system.BeginTransactionAsync(write: true, token);
        const string Collection = "first_presence_probe";
        using var value = JsonDocument.Parse("""{"value":true}""");
        if (replicated)
        {
            var at = new EntryVersion(FeedCursor.At(DateTimeOffset.UtcNow), ReplicaId.Of(4518, 9, 1));
            _ = await write.JoinAsync(Collection, EntityIds.New().ToString(), DocumentState.Bottom.Put(at, value.RootElement), at, token);
        }
        else
        {
            _ = await write.PutAsync(Collection, EntityIds.New().ToString(), """{"value":true}"""u8.ToArray(), token);
            // Group commit flushes local states before publication, just as Join already does.
            await ((SqliteStorageTransaction)write).FlushStatesAsync();
        }

        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = engine.Io.RunAsync(() =>
        {
            entered.TrySetResult();
            release.Wait(token);
        }, token);
        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<bool>? probe = null;
        try
        {
            await entered.Task.WaitAsync(token);
            probe = engine.MayHaveSystemCollectionStateAsync(Collection, discovery.Token).AsTask();
            await Assert.That(engine.IoAdmittedCount).IsEqualTo(2);
            // The first probe waits for the held writer without queuing an older read snapshot.
            await Assert.That(engine.Io.Queued).IsEqualTo(0);
            await Assert.That(probe.IsCompleted).IsFalse();
            release.Set();
            await blocked;
            _ = await write.CommitAsync(token);
            await write.DisposeAsync();
            await Assert.That(await probe).IsTrue();

            await using var held = await system.BeginTransactionAsync(write: false, token);
            await Assert.That(await engine.MayHaveSystemCollectionStateAsync(Collection, token)).IsTrue();
            await Assert.That(engine.IoAdmittedCount).IsEqualTo(1);
            await Assert.That(await held.GetAsync("catalog", "version", token)).IsNull();
        }
        finally
        {
            release.Set();
            await blocked;
            await discovery.CancelAsync();
            if (probe is not null)
            {
                try
                {
                    _ = await probe;
                }
                catch (OperationCanceledException) when (discovery.IsCancellationRequested)
                {
                }
            }
        }
    }
}
