using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class ReplicaSnapshotTests
{
    [Test]
    public async Task A_snapshot_keeps_its_states_and_origin_cuts_pinned_across_pages()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync();
        var database = await DatabaseAsync(api);
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, token))!;
        var origin = ReplicaId.Of(99, 1, 0);
        var stamp = new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16);
        var sink = api.Server.Services.GetRequiredService<IChangeGroupSink>();
        var seeded = Enumerable.Range(0, 1025).Select(_ => new RepairState("items", EntityIds.New().ToString(), State(origin, stamp, "{\"value\":1}"))).ToArray();
        await ApplyAsync(sink, database, origin, stamp, seeded, token);
        var source = api.Server.Services.GetRequiredService<IReplicaSnapshotSource>();
        await using var pages = source.SnapshotAsync(database, store.Incarnation, token).GetAsyncEnumerator(token);
        await Assert.That(await pages.MoveNextAsync()).IsTrue();
        await Assert.That(pages.Current.States.Count).IsEqualTo(1024);
        List<RepairState> exported = [.. pages.Current.States];
        var later = EntityIds.New().ToString();
        var next = new Hlc(stamp.Value + 1);
        await ApplyAsync(sink, database, origin, next, [new RepairState("items", later, State(origin, next, "{\"value\":2}"))], token);
        ReplicaSnapshotPage? terminal = null;
        while (await pages.MoveNextAsync())
        {
            exported.AddRange(pages.Current.States);
            terminal = pages.Current;
        }

        await Assert.That(exported.Any(state => state.Id == later)).IsFalse();
        await Assert.That(exported.Count(state => state.Collection == "items")).IsEqualTo(1025);
        await Assert.That(terminal!.Applied![origin]).IsEqualTo(stamp);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task An_import_commits_old_coverage_only_after_the_whole_snapshot_and_clean_EOF(int interruption)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync();
        var (source, target) = (await DatabaseAsync(api), await DatabaseAsync(api));
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var sourceStore = (await engine.OpenDatabaseAsync(source, token))!;
        var targetStore = (await engine.OpenDatabaseAsync(target, token))!;
        var origin = ReplicaId.Of(99, 2, 0);
        var stamp = new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16);
        var (arriving, survivor) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        var sink = api.Server.Services.GetRequiredService<IChangeGroupSink>();
        var incoming = Enumerable.Range(0, 1025).Select(index => new RepairState("items", index == 0 ? arriving : EntityIds.New().ToString(), State(origin, stamp, "{\"value\":1}"))).ToArray();
        await ApplyAsync(sink, source, origin, stamp, incoming, token);
        _ = await sink.RepairAsync(target, targetStore.Incarnation, [new RepairState("items", survivor, State(origin, stamp, "{\"survivor\":true}"))], token);
        var exporter = api.Server.Services.GetRequiredService<IReplicaSnapshotSource>();
        var importer = api.Server.Services.GetRequiredService<IReplicaSnapshotImporter>();
        if (interruption == 0)
        {
            await Assert.That(await importer.ImportAsync(target, targetStore.Incarnation, Pages(token), new HashSet<ulong> { origin.Value }, token)).IsTrue();
        }
        else
        {
            _ = await Assert.ThrowsAsync<IOException>(async () =>
                _ = await importer.ImportAsync(target, targetStore.Incarnation, Pages(token), new HashSet<ulong> { origin.Value }, token));
        }

        await using var read = await targetStore.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetAsync("items", survivor, token)).IsNotNull();
        var coverage = await read.GetAppliedAsync(token);
        await Assert.That(coverage.ContainsKey(origin)).IsEqualTo(interruption == 0);
        foreach (var state in incoming)
        {
            await Assert.That(await read.GetAsync("items", state.Id, token) is not null).IsEqualTo(interruption == 0);
        }

        async IAsyncEnumerable<ReplicaSnapshotPage> Pages([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var page in exporter.SnapshotAsync(source, sourceStore.Incarnation, cancellationToken))
            {
                if (interruption == 1 && page.Applied is not null)
                {
                    throw new IOException("connection lost before terminal frame");
                }

                yield return page;
            }

            if (interruption == 2)
            {
                throw new IOException("error trailers after terminal frame");
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Collected_absence_never_erases_an_unproven_live_survivor(bool missingDeletion)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync();
        var (source, target) = (await DatabaseAsync(api), await DatabaseAsync(api));
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var sourceStore = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(source, token))!;
        var targetStore = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(target, token))!;
        var origin = ReplicaId.Of(99, 3, 0);
        var first = new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16);
        var deletedAt = new Hlc(first.Value + 1);
        var sourceFloor = new Hlc(first.Value + 2);
        var targetFloor = new Hlc(first.Value + 3);
        var (kept, ghost, collected) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        var live = State(origin, first, "{\"retained\":true}");
        var dead = live.Delete(new EntryVersion(deletedAt, origin));
        var sink = api.Server.Services.GetRequiredService<IChangeGroupSink>();
        await ApplyAsync(sink, source, origin, deletedAt, [new RepairState("items", kept, live), new RepairState("items", ghost, dead)], token);
        _ = await sink.RepairAsync(target, targetStore.Incarnation,
            [new RepairState("items", ghost, missingDeletion ? live : dead), new RepairState("items", collected, dead)], token);
        await CollectAsync(sourceStore, sourceFloor, token);
        // Raise target's floor without discarding ghost's evidence, which the source's gap scan must still inspect.
        await using (var transaction = (TursoStorageTransaction)await targetStore.BeginDurableTransactionAsync(token))
        {
            await transaction.InheritCollectedBeforeAsync(targetFloor, token);
            _ = await transaction.CommitAsync(token);
        }

        var importer = api.Server.Services.GetRequiredService<IReplicaSnapshotImporter>();
        var pages = api.Server.Services.GetRequiredService<IReplicaSnapshotSource>().SnapshotAsync(source, sourceStore.Incarnation, token);
        await Assert.That(await importer.ImportAsync(target, targetStore.Incarnation, pages, new HashSet<ulong> { origin.Value }, token)).IsEqualTo(!missingDeletion);
        await using var read = await targetStore.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetAsync("items", ghost, token) is not null).IsEqualTo(missingDeletion);
        await Assert.That(await read.GetAsync("items", kept, token) is not null).IsEqualTo(!missingDeletion)
            .Because("a retained source survivor is imported even below the receiver's collected-state floor");
        await Assert.That((await read.GetAppliedAsync(token)).ContainsKey(origin)).IsEqualTo(!missingDeletion);
    }

    private static async Task CollectAsync(TursoDatabaseStore store, Hlc floor, CancellationToken token)
    {
        await using var transaction = (TursoStorageTransaction)await store.BeginDurableTransactionAsync(token);
        await Assert.That(await transaction.CollectAsync(floor, token)).IsGreaterThan(0);
        _ = await transaction.CommitAsync(token);
    }

    private static async Task<TenantContext> DatabaseAsync(ManagementApi api)
    {
        var database = await api.CreateManagedDatabaseAsync();
        return new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
    }

    private static DocumentState State(ReplicaId origin, Hlc stamp, string json)
    {
        using var body = JsonDocument.Parse(json);
        return DocumentState.Bottom.Put(new EntryVersion(stamp, origin), body.RootElement);
    }

    private static async Task ApplyAsync(IChangeGroupSink sink, TenantContext database, ReplicaId origin, Hlc stamp,
        IReadOnlyList<RepairState> states, CancellationToken token)
    {
        foreach (var chunk in states.Chunk(ChangeGroups.MaxDeltasOf(database)))
        {
            _ = await sink.ApplyAsync(database, origin, [new OriginGroup(1, [.. chunk.Select(state => new ChangeDelta(state.Collection, state.Id, stamp, state.State))])], default, token);
        }
    }
}
