using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Replication;

public sealed class MovingCertifiedGcTests
{
    [Test]
    public async Task Certified_refill_removes_a_live_key_collected_at_the_source_after_its_first_import()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var probe = new ImportProbe();
        await using var cluster = await StartAsync(probe);
        var source = cluster["gru"];
        var receiver = cluster["sjc"];
        var collected = EntityIds.New().ToString();
        var baseline = EntityIds.New().ToString();
        var newer = EntityIds.New().ToString();
        var live = Put(1000, """{"old":true}""");
        await RepairAsync(source, new("items", collected, live), new("items", baseline, Put(500, """{"baseline":true}""")));
        await RepairAsync(receiver, new RepairState("items", newer, Put(4000, """{"newer":true}""")));
        var imports = 0;
        probe.AfterImport = async (database, states, cancellationToken) =>
        {
            if (database != cluster.Database || !states.Any(state => state.Collection == "items" && state.Id == collected)
                || Interlocked.CompareExchange(ref imports, 1, 0) != 0)
            {
                return;
            }

            // The receiver committed the live key; source collection now forces a retry of its bucket.
            await Assert.That(await receiver.ReadAsync(collected)).IsEqualTo("""{"old":true}""");
            await RepairAsync(source, new RepairState("items", collected, live.Delete(At(2000))));
            var engine = source.Api.Server.Services.GetRequiredService<SqliteStorageEngine>();
            var sourceStore = (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, cancellationToken))!;
            await using var collect = (SqliteStorageTransaction)await sourceStore.BeginTransactionAsync(write: true, cancellationToken);
            await Assert.That(await collect.CollectAsync(new Hlc(3000), cancellationToken)).IsEqualTo(1);
            _ = await collect.CommitAsync(cancellationToken);
        };
        var incarnation = await receiver.IncarnationAsync();
        await receiver.Api.Server.Services.GetRequiredService<IReplicaCopyFaults>()
            .RecordAsync(cluster.Database, incarnation, "a fixture copy requiring certified refill", token);

        var outcome = await receiver.Api.Server.Services.GetRequiredService<IReplicaRebuild>()
            .RebuildAsync(cluster.Database, incarnation, token);

        await Assert.That(imports).IsEqualTo(1).Because("source collection ran immediately after the live baseline page committed");
        await Assert.That(await source.HasStateAsync("items", collected)).IsFalse();
        await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
        await Assert.That(await receiver.ReadAsync(baseline)).IsEqualTo("""{"baseline":true}""");
        await Assert.That(await receiver.ReadAsync(newer)).IsEqualTo("""{"newer":true}""");
        await Assert.That(await receiver.ReadAsync(collected)).IsNull();
        await Assert.That(await receiver.HasStateAsync("items", collected)).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task A_trailing_collection_metadata_gap_preserves_source_present_documents_and_indexes_and_removes_empty_collections(
        bool survivor, bool retainedIndex)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var probe = new ImportProbe();
        await using var cluster = await StartAsync(probe);
        var source = cluster["gru"];
        var receiver = cluster["sjc"];
        const string Id = "00000000000000000000000000";
        const string Index = "by_value_2";
        await Assert.That(SqliteStorageTransaction.Bucket("items", Id))
            .IsLessThan(SqliteStorageTransaction.Bucket(DocumentDelta.Collections, "items"));
        await Assert.That(SqliteStorageTransaction.Bucket(DocumentDelta.Indexes, Index))
            .IsLessThan(SqliteStorageTransaction.Bucket(DocumentDelta.Collections, "items"));
        var metadata = Put(1000, $$"""{"created_at":1,"entity_id":"{{EntityIds.New()}}"}""");
        await RepairAsync(source, new(DocumentDelta.Collections, "items", metadata),
            new("items", Id, Put(survivor ? 2500UL : 1500UL, """{"survivor":true}""")));
        var imports = 0;
        var retries = 0;
        probe.AfterImport = async (database, states, cancellationToken) =>
        {
            if (database != cluster.Database)
            {
                return;
            }

            if (states.Any(state => state.Collection == DocumentDelta.Collections && state.Id == "items")
                && Interlocked.CompareExchange(ref imports, 1, 0) == 0)
            {
                await Assert.That(await receiver.HasStateAsync(DocumentDelta.Collections, "items")).IsTrue();
                await Assert.That(await receiver.ReadAsync(Id)).IsEqualTo("""{"survivor":true}""");
                await RepairAsync(source, new RepairState(DocumentDelta.Collections, "items", metadata.Delete(At(2000))));
                var engine = source.Api.Server.Services.GetRequiredService<SqliteStorageEngine>();
                var store = (await engine.OpenDatabaseAsync(database, cancellationToken))!;
                await using (var collect = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: true, cancellationToken))
                {
                    await Assert.That(await collect.CollectAsync(new Hlc(3000), cancellationToken)).IsEqualTo(1);
                    _ = await collect.CommitAsync(cancellationToken);
                }

                if (retainedIndex)
                {
                    await RepairAsync(source, new RepairState(DocumentDelta.Indexes, Index,
                        Put(4000, """{"collection":"items","fields":["value"]}""")));
                }
            }
            else if (states.Any(state => state.Collection == "items" && state.Id == Id
                || state.Collection == DocumentDelta.Indexes && state.Id == Index))
            {
                Interlocked.Increment(ref retries);
                await Assert.That(await receiver.HasStateAsync(DocumentDelta.Collections, "items")).IsTrue();
                await Assert.That(await receiver.ReadAsync(Id)).IsEqualTo("""{"survivor":true}""");
            }
        };
        var incarnation = await receiver.IncarnationAsync();
        await receiver.Api.Server.Services.GetRequiredService<IReplicaCopyFaults>()
            .RecordAsync(cluster.Database, incarnation, "a fixture copy requiring certified refill", token);

        var outcome = await receiver.Api.Server.Services.GetRequiredService<IReplicaRebuild>()
            .RebuildAsync(cluster.Database, incarnation, token);

        await Assert.That(imports).IsEqualTo(1);
        await Assert.That(retries).IsEqualTo(survivor || retainedIndex ? 1 : 0)
            .Because("a source-present document or index produces a second source page before the trailing metadata gap");
        await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
        await Assert.That(await source.HasStateAsync(DocumentDelta.Collections, "items")).IsFalse();
        var expected = survivor ? """{"survivor":true}""" : null;
        await Assert.That(await source.ReadAsync(Id)).IsEqualTo(expected);
        foreach (var restart in new[] { false, true })
        {
            if (restart)
            {
                await receiver.StopAsync();
                await receiver.StartAsync();
            }

            await Assert.That(await receiver.HasStateAsync(DocumentDelta.Collections, "items")).IsFalse();
            await Assert.That(await receiver.ReadAsync(Id)).IsEqualTo(expected);
            await Assert.That(await receiver.HasStateAsync("items", Id)).IsEqualTo(survivor);
            await using var read = await (await receiver.StoreAsync()).BeginTransactionAsync(write: false, token);
            await Assert.That((await read.ListCollectionsAsync(null, 100, token)).Collections.Any(collection => collection.Name == "items"))
                .IsEqualTo(survivor || retainedIndex);
            await Assert.That((await read.ListIndexesAsync(token)).Any(index => index.Name == Index && index.Collection == "items"))
                .IsEqualTo(retainedIndex);
        }
    }

    private static Task<PeerStreamTests.Cluster> StartAsync(ImportProbe probe) =>
        PeerStreamTests.Cluster.StartAsync(cdc: false, clock: null, configure: (region, services) =>
        {
            foreach (var recovery in services.Where(service => service.ServiceType == typeof(IHostedService)
                && service.ImplementationType == typeof(RebuildRecovery)).ToArray())
            {
                services.Remove(recovery);
            }

            if (region == "sjc")
            {
                var repair = services.Last(service => service.ServiceType == typeof(IRebuildRepair));
                services.AddSingleton<IRebuildRepair>(provider => new AfterImportRepair(
                    (IRebuildRepair)(repair.ImplementationInstance ?? repair.ImplementationFactory?.Invoke(provider)
                        ?? ActivatorUtilities.CreateInstance(provider, repair.ImplementationType!)), probe));
            }
        }, "gru", "sjc");

    private static async Task RepairAsync(PeerStreamTests.Machine machine, params RepairState[] states) =>
        _ = await machine.Api.Server.Services.GetRequiredService<IChangeGroupSink>().RepairAsync(
            machine.Cluster.Database, (await machine.StoreAsync()).Incarnation, states, TestContext.Current!.Execution.CancellationToken);

    private static EntryVersion At(ulong stamp) => new(new Hlc(stamp), ReplicaId.Of(99, 1, 0));

    private static DocumentState Put(ulong stamp, string json)
    {
        using var body = JsonDocument.Parse(json);
        return DocumentState.Bottom.Put(At(stamp), body.RootElement);
    }

    private sealed class ImportProbe
    {
        public Func<TenantContext, IReadOnlyList<RepairState>, CancellationToken, Task>? AfterImport { get; set; }
    }

    private sealed class AfterImportRepair(IRebuildRepair inner, ImportProbe probe) : IRebuildRepair
    {
        public async ValueTask<int?> RefillRebuildAsync(TenantContext database, Ulid newIncarnation, IReadOnlyList<int> buckets,
            BucketCursor? after, IReadOnlyList<RepairState> states, Hlc collectedBefore, CancellationToken cancellationToken)
        {
            var changed = await inner.RefillRebuildAsync(database, newIncarnation, buckets, after, states, collectedBefore, cancellationToken);
            if (changed is not null && probe.AfterImport is { } callback)
            {
                await callback(database, states, cancellationToken);
            }

            return changed;
        }

        public async ValueTask<int?> RepairRebuildAsync(TenantContext database, Ulid newIncarnation,
            IReadOnlyList<RepairState> states, CancellationToken cancellationToken)
        {
            var changed = await inner.RepairRebuildAsync(database, newIncarnation, states, cancellationToken);
            if (changed is not null && probe.AfterImport is { } callback)
            {
                await callback(database, states, cancellationToken);
            }

            return changed;
        }

        public ValueTask<bool> RecordRefillAsync(TenantContext database, Ulid newIncarnation, string sourceMachine,
            Ulid sourceIncarnation, long head, Hlc collectedBefore, CancellationToken cancellationToken) =>
            inner.RecordRefillAsync(database, newIncarnation, sourceMachine, sourceIncarnation, head, collectedBefore, cancellationToken);

        public ValueTask<bool> CompleteRebuildAsync(TenantContext database, Ulid oldIncarnation, Ulid newIncarnation,
            CancellationToken cancellationToken) => inner.CompleteRebuildAsync(database, oldIncarnation, newIncarnation, cancellationToken);
    }
}
