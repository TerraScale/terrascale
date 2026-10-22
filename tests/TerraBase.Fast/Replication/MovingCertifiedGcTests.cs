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
        await using var cluster = await PeerStreamTests.Cluster.StartAsync(cdc: false, clock: null,
            configure: (region, services) =>
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
