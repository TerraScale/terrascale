using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Storage;
using TerraBase.Features.Replication;
using TerraBase.Features.Webhooks;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class StorageChangeGroupSinkTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Slow_per_state_work_closes_the_chunk_before_its_item_limit(bool repair)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: clock, services: services =>
        {
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock);
            services.AddSingleton<IWebhookReplicaAdmission>(provider => new TimedAdmission(provider.GetRequiredService<WebhookReplicaAdmission>(), clock));
        });
        var store = (TursoDatabaseStore)await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(token);
        var sink = api.Server.Services.GetRequiredService<IChangeGroupSink>();
        var origin = ReplicaId.Of(99, 6, 0);
        using var body = JsonDocument.Parse("""{"timed":true}""");
        var states = Enumerable.Range(0, 20).Select(index => new RepairState("sink_time", Ulid.NewUlid().ToString(),
            DocumentState.Bottom.Put(new EntryVersion(new Hlc((ulong)index + 1000), origin), body.RootElement))).ToArray();
        using var holds = new WriterHoldTests.WriterHolds(api.Server);
        var before = store.CommitSequence;
        if (repair)
        {
            await Assert.That(await sink.RepairAsync(ChangeGroups.System, store.Incarnation, states, token)).IsEqualTo(states.Length);
        }
        else
        {
            var groups = states.Select((state, index) => new OriginGroup(index + 1,
                [new ChangeDelta(state.Collection, state.Id, state.State.MaxStamp, state.State)])).ToArray();
            _ = await sink.ApplyAsync(ChangeGroups.System, origin, groups, default, token);
        }

        await Assert.That(store.CommitSequence - before).IsGreaterThanOrEqualTo(4L);
        await Assert.That(holds.Measured("a durable write transaction")).Contains(0.25);
        await Assert.That(holds.Longest).IsLessThan(1);
    }

    [Test]
    public async Task An_oversized_group_stays_atomic_and_a_retry_resumes_from_the_committed_cut()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        Action? committed = null;
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(DateTimeOffset.UtcNow), services: services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
            {
                if (step == "replication.apply.committed")
                {
                    committed?.Invoke();
                }
            }));
        var store = await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(token);
        var sink = api.Server.Services.GetRequiredService<IChangeGroupSink>();
        var origin = ReplicaId.Of(99, 5, 0);
        var first = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16;
        using var body = JsonDocument.Parse("""{"retry":true}""");
        var offset = 0;
        var groups = new[] { 8, 300, 8 }.Select((size, index) => new OriginGroup(index + 1,
            [.. Enumerable.Range(0, size).Select(_ =>
            {
                var stamp = new Hlc(first + (ulong)++offset);
                return new ChangeDelta("sink_retry", Ulid.NewUlid().ToString(), stamp,
                    DocumentState.Bottom.Put(new EntryVersion(stamp, origin), body.RootElement));
            })])).ToArray();
        var progress = new ApplyProgress();
        committed = () => throw new IOException("stream lost after the first durable chunk");
        _ = await Assert.ThrowsAsync<IOException>(async () =>
            _ = await sink.ApplyAsync(ChangeGroups.System, origin, groups, default, token, progress));
        var cut = groups[0].Deltas[^1].Stamp;
        await using (var read = await store.BeginTransactionAsync(write: false, token))
        {
            await Assert.That((await read.GetAppliedAsync(token))[origin]).IsEqualTo(cut);
            await Assert.That(await read.GetAsync("sink_retry", groups[1].Deltas[0].Id, token)).IsNull();
        }

        await Assert.That(progress.Value).IsEqualTo(8L);
        var boundaries = new List<long>();
        progress = new ApplyProgress();
        committed = () => boundaries.Add(progress.Value);
        await Assert.That(await sink.ApplyAsync(ChangeGroups.System, origin, groups[1..], cut, token, progress)).IsEqualTo(groups[^1].Deltas[^1].Stamp);
        await Assert.That(boundaries.SequenceEqual([300L, 308L])).IsTrue();
        await using var final = await store.BeginTransactionAsync(write: false, token);
        await Assert.That((await final.GetAppliedAsync(token))[origin]).IsEqualTo(groups[^1].Deltas[^1].Stamp);
        foreach (var delta in groups.SelectMany(group => group.Deltas))
        {
            await Assert.That(await final.GetAsync(delta.Collection, delta.Id, token)).IsNotNull();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_large_system_page_commits_bounded_chunks_and_preserves_every_state(bool repair)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(DateTimeOffset.UtcNow), services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, TimeProvider.System));
        var store = (TursoDatabaseStore)await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(token);
        var sink = api.Server.Services.GetRequiredService<IChangeGroupSink>();
        var origin = ReplicaId.Of(99, 4, 0);
        var first = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16;
        var faker = new Faker();
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(new { value = faker.Random.AlphaNumeric(128) }));
        var states = Enumerable.Range(0, 4096).Select(index => new RepairState("sink_bounds", Ulid.NewUlid().ToString(),
            DocumentState.Bottom.Put(new EntryVersion(new Hlc(first + (ulong)index), origin), body.RootElement))).ToArray();
        using var holds = new WriterHoldTests.WriterHolds(api.Server);
        var before = store.CommitSequence;
        if (repair)
        {
            await Assert.That(await sink.RepairAsync(ChangeGroups.System, store.Incarnation, states, token)).IsEqualTo(states.Length);
        }
        else
        {
            var groups = states.Chunk(8).Select((chunk, index) => new OriginGroup(index + 1,
                [.. chunk.Select(state => new ChangeDelta(state.Collection, state.Id, state.State.MaxStamp, state.State))])).ToArray();
            await Assert.That(await sink.ApplyAsync(ChangeGroups.System, origin, groups, default, token)).IsEqualTo(states[^1].State.MaxStamp);
        }

        var commits = store.CommitSequence - before;
        Console.WriteLine($"SINK repair={repair}: {states.Length} states, {commits} commits, max writer hold {holds.Longest:F6} s");
        await using var read = await store.BeginTransactionAsync(write: false, token);
        foreach (var state in states)
        {
            await Assert.That((await read.GetAsync(state.Collection, state.Id, token))?.Json.ToArray().SequenceEqual(state.State.Materialize()!)).IsTrue();
        }

        var applied = await read.GetAppliedAsync(token);
        if (repair)
        {
            await Assert.That(applied.ContainsKey(origin)).IsFalse().Because("origin-0 repair never advances a stream's cut");
        }
        else
        {
            await Assert.That(applied[origin]).IsEqualTo(states[^1].State.MaxStamp);
        }

        await Assert.That(commits).IsGreaterThanOrEqualTo(16L).Because("4096 states require at least sixteen 256-item transactions");
        await Assert.That(holds.Longest).IsGreaterThan(0);
        await Assert.That(holds.Longest).IsLessThan(1);
    }

    // An existing per-state admission seam spends deterministic time while all storage joins still run against Turso.
    private sealed class TimedAdmission(IWebhookReplicaAdmission inner, FakeTimeProvider clock) : IWebhookReplicaAdmission
    {
        public bool Decides(string collection)
        {
            if (collection == "sink_time")
            {
                clock.Advance(TimeSpan.FromMilliseconds(50));
            }

            return inner.Decides(collection);
        }

        public ValueTask<bool> AdmitAsync(IStorageTransaction transaction, string collection, string id, DocumentState state, CancellationToken cancellationToken) =>
            inner.AdmitAsync(transaction, collection, id, state, cancellationToken);
    }
}
