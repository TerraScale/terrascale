using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Hosting;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Testing;
using TerraBase.Features.Replication;

namespace TerraBase.Fast.Replication;

public sealed class SnapshotImportSafetyTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Collection_during_staging_cannot_resurrect_a_deleted_document(bool inheritedFloor)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync();
        var id = await api.CreateOrganizationAsync("deleted while receiving a baseline");
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        var floor = new Hlc(engine.Member.Clock.Tick().Value + (10_000UL << 16));
        if (inheritedFloor)
        {
            await using var write = (TursoStorageTransaction)await store.BeginDurableTransactionAsync(token);
            await write.InheritCollectedBeforeAsync(floor, token);
            _ = await write.CommitAsync(token);
        }

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DocumentState? stagedState = null;
        var import = api.Server.Services.GetRequiredService<IReplicaSnapshotImporter>()
            .ImportAsync(ChangeGroups.System, store.Incarnation, Pages(token), new HashSet<ulong>(), token);
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            await using (var write = (TursoStorageTransaction)await store.BeginDurableTransactionAsync(token))
            {
                if (inheritedFloor)
                {
                    // A late replicated tombstone can be collected without advancing an already inherited floor.
                    var at = new EntryVersion(new Hlc(floor.Value - 1), engine.ReplicaId);
                    _ = await write.JoinAsync("organizations", id, stagedState!.Delete(at), at, token);
                }
                else
                {
                    _ = await write.DeleteAsync("organizations", id, token);
                }
                var deleted = (await write.GetVersionAsync("organizations", id, token))!.Value.Version;
                floor = inheritedFloor ? floor : new Hlc(unchecked((ulong)deleted) + 1);
                _ = await write.CommitAsync(token);
            }
            await using var collect = (TursoStorageTransaction)await store.BeginDurableTransactionAsync(token);
            await Assert.That(await collect.CollectAsync(floor, token)).IsGreaterThan(0);
            _ = await collect.CommitAsync(token);
        }
        finally
        {
            release.TrySetResult();
            _ = await import;
        }
        await using var read = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetAsync("organizations", id, token)).IsNull();
        await Assert.That(await import).IsFalse();

        async IAsyncEnumerable<ReplicaSnapshotPage> Pages([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var first = true;
            await foreach (var page in api.Server.Services.GetRequiredService<IReplicaSnapshotSource>()
                .SnapshotAsync(ChangeGroups.System, store.Incarnation, cancellationToken))
            {
                stagedState ??= page.States.FirstOrDefault(state => state.Collection == "organizations" && state.Id == id)?.State;
                yield return page;
                if (first)
                {
                    first = false;
                    paused.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }
            }
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Cancelled_or_expired_imports_rollback_and_release_the_writer(int expiry)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var reached = false;
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
            {
                if (step == "replication.snapshot.page" && !reached)
                {
                    reached = true;
                    if (expiry == 0)
                    {
                        caller.Cancel();
                    }
                    else if (expiry == 1)
                    {
                        clock.Advance(TursoReplicaSnapshotImporter.BaseReplayTimeout + TimeSpan.FromSeconds(1));
                    }
                }
            });
        });
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        var origin = ReplicaId.Of(99, 5, 0);
        var stamp = engine.Member.Clock.Tick();
        var id = Ulid.NewUlid().ToString();
        using var body = System.Text.Json.JsonDocument.Parse("""{"staged":true}""");
        var state = DocumentState.Bottom.Put(new EntryVersion(stamp, origin), body.RootElement);
        var importer = api.Server.Services.GetRequiredService<IReplicaSnapshotImporter>();
        await Assert.ThrowsAsync<OperationCanceledException>(() => importer.ImportAsync(ChangeGroups.System, store.Incarnation,
            Pages(caller.Token), new HashSet<ulong> { origin.Value }, caller.Token));
        await Assert.That(reached).IsEqualTo(expiry != 2);
        await using (var write = await store.BeginDurableTransactionAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(1), token))
        {
            _ = await write.CommitAsync(token);
        }
        await using var read = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetAsync("snapshot_cancel", id, token)).IsNull();
        await Assert.That((await read.GetAppliedAsync(token)).ContainsKey(origin)).IsFalse();
        await Assert.That(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, ".snapshot-import-*")).IsEmpty();

        async IAsyncEnumerable<ReplicaSnapshotPage> Pages([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ReplicaSnapshotPage([new RepairState("snapshot_cancel", id, state)]);
            if (expiry == 2)
            {
                clock.Advance(TursoReplicaSnapshotImporter.ImportTimeout + TimeSpan.FromSeconds(1));
                cancellationToken.ThrowIfCancellationRequested();
            }
            await Task.CompletedTask;
            yield return new ReplicaSnapshotPage([], new Dictionary<ReplicaId, Hlc> { [origin] = stamp });
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Staged_work_extends_the_replay_budget_but_never_past_its_cap(bool capped)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var reached = false;
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
            {
                if (step == "replication.snapshot.page" && !reached)
                {
                    reached = true;
                    clock.Advance((capped ? TursoReplicaSnapshotImporter.MaxReplayTimeout : TursoReplicaSnapshotImporter.BaseReplayTimeout)
                        + TimeSpan.FromSeconds(1));
                }
            });
        });
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        var origin = ReplicaId.Of(99, 7, 0);
        var stamp = engine.Member.Clock.Tick();
        using var body = System.Text.Json.JsonDocument.Parse("""{"staged":true}""");
        var state = DocumentState.Bottom.Put(new EntryVersion(stamp, origin), body.RootElement);
        var states = Enumerable.Range(0, capped ? 65_535 : 1024).Select(_ => new RepairState("snapshot_budget", Ulid.NewUlid().ToString(), state))
            .OrderBy(row => TursoStorageTransaction.Bucket(row.Collection, row.Id)).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
        var importer = api.Server.Services.GetRequiredService<IReplicaSnapshotImporter>();
        if (capped)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(ImportAsync);
        }
        else
        {
            await Assert.That(await ImportAsync()).IsTrue();
        }
        await Assert.That(reached).IsTrue();
        await using (var write = await store.BeginDurableTransactionAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(1), token))
        {
            _ = await write.CommitAsync(token);
        }
        await using var read = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetAsync("snapshot_budget", states[0].Id, token) is not null).IsEqualTo(!capped);
        await Assert.That((await read.GetAppliedAsync(token)).ContainsKey(origin)).IsEqualTo(!capped);

        Task<bool> ImportAsync()
        {
            return importer.ImportAsync(ChangeGroups.System, store.Incarnation, Pages(token), new HashSet<ulong> { origin.Value }, token);
        }

        async IAsyncEnumerable<ReplicaSnapshotPage> Pages([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Exactly 65,536 frames including data and terminal: frame work alone exceeds the five-minute cap.
            if (capped)
            {
                foreach (var row in states)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new ReplicaSnapshotPage([row]);
                }
            }
            else
            {
                yield return new ReplicaSnapshotPage(states);
            }
            await Task.CompletedTask;
            yield return new ReplicaSnapshotPage([], new Dictionary<ReplicaId, Hlc> { [origin] = stamp });
        }
    }

    [Test]
    public async Task Snapshot_witnesses_allow_stable_progress_without_releasing_a_live_peer()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now.AddDays(-2));
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            // This test supplies a fixed directory; the background membership refresh must not replace it.
            var streams = services.Single(service => service.ServiceType == typeof(IHostedService)
                && service.ImplementationFactory?.Method.ReturnType == typeof(PeerStreams));
            services.Remove(streams);
        });
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(TestContext.Current!.Execution.CancellationToken);
        var token = TestContext.Current!.Execution.CancellationToken;
        var retired = ReplicaId.Of(99, 6, 0);
        var live = ReplicaId.Of(99, 6, 1);
        var before = engine.Member.Clock.Tick();
        engine.Member.Presence.Listed([("live", "test-region")], before.Milliseconds);
        engine.Member.Presence.Heard("live", live, before.Milliseconds);
        clock.SetUtcNow(now);
        var current = engine.Member.Clock.Tick();
        await using (var write = (TursoStorageTransaction)await store.BeginDurableTransactionAsync(token))
        {
            await write.RecordAppliedAsync(retired, before, token);
            _ = await write.RecordAckedAsync(live, store.Incarnation, 0, default, null, now, token);
            _ = await write.CommitAsync(token);
        }
        await Assert.That(await api.Server.Services.GetRequiredService<IReplicaSnapshotImporter>().ImportAsync(ChangeGroups.System,
            store.Incarnation, Pages(token), new HashSet<ulong> { retired.Value }, token)).IsTrue();
        await using (var read = (TursoStorageTransaction)await store.BeginTransactionAsync(write: false, token))
        {
            await Assert.That((await read.GetStableAsync(current, token)).Value).IsEqualTo(0UL);
            await Assert.That((await read.GetHorizonAsync(current, token)).Value).IsEqualTo(0UL);
        }
        await using (var write = (TursoStorageTransaction)await store.BeginDurableTransactionAsync(token))
        {
            await write.RecordAppliedAsync(engine.ReplicaId, current, token);
            await write.RecordStableAsync(engine.ReplicaId, current, now, token);
            await write.RecordAppliedAsync(live, current, token);
            await write.RecordStableAsync(live, current, now, token);
            _ = await write.CommitAsync(token);
        }
        await using var final = (TursoStorageTransaction)await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await final.GetStableAsync(current, token)).IsEqualTo(current);
        await Assert.That((await final.GetHorizonAsync(current, token)).Value).IsGreaterThan(0UL);

        async IAsyncEnumerable<ReplicaSnapshotPage> Pages([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return new ReplicaSnapshotPage([], new Dictionary<ReplicaId, Hlc> { [retired] = before });
        }
    }
}
