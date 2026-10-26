using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Storage;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class StorageChangeGroupSinkTests
{
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
}
