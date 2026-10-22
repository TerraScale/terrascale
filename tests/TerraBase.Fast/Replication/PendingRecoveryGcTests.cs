using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Primitives;

namespace TerraBase.Fast.Replication;

public sealed class PendingRecoveryGcTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_old_live_side_state_keeps_recovery_pending_and_the_original_unchanged(bool mixed)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await PeerStreamTests.Cluster.StartAsync(cdc: false, clock: null,
            configure: (_, services) =>
            {
                foreach (var recovery in services.Where(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType == typeof(RebuildRecovery)).ToArray())
                {
                    services.Remove(recovery);
                }
            }, "gru", "sjc");
        var source = cluster["gru"];
        var receiver = cluster["sjc"];
        var id = EntityIds.New().ToString();
        using var body = JsonDocument.Parse("""{"old":true}""");
        var old = DocumentState.Bottom.Put(At(1000), body.RootElement);
        var incoming = mixed ? old.Set(At(4000), "new", body.RootElement.GetProperty("old")) : old;
        var sourceSink = source.Api.Server.Services.GetRequiredService<IChangeGroupSink>();
        _ = await sourceSink.RepairAsync(cluster.Database, await source.IncarnationAsync(),
            [new("items", EntityIds.New().ToString(), old.Delete(At(2000)))], token);
        var store = (SqliteDatabaseStore)(await source.Api.Server.Services.GetRequiredService<SqliteStorageEngine>()
            .OpenDatabaseAsync(cluster.Database, token))!;
        await using (var collect = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: true, token))
        {
            await Assert.That(await collect.CollectAsync(new Hlc(3000), token)).IsEqualTo(1);
            _ = await collect.CommitAsync(token);
        }

        _ = await receiver.Api.Server.Services.GetRequiredService<IChangeGroupSink>().RepairAsync(cluster.Database,
            await receiver.IncarnationAsync(), [new("items", id, incoming)], token);
        var provider = receiver.Api.Server.Services;
        var original = await receiver.IncarnationAsync();
        await provider.GetRequiredService<IReplicaCopyFaults>().RecordAsync(cluster.Database, original, "a fixture requiring recovery", token);
        var storage = provider.GetRequiredService<IRebuildStorage>();
        var ledger = provider.GetRequiredService<IRebuildLedger>();
        var side = storage.SidePath(cluster.Database, original);
        var record = new RebuildRecord(cluster.Database, original, side, RebuildPhase.Pending, null);
        await ledger.SaveAsync(record, token);
        var rebuilt = await storage.SwapAsync(cluster.Database, original, side, token)
            ?? throw new InvalidOperationException("the recovery fixture did not swap its original copy");
        await ledger.SaveAsync(record with { Phase = RebuildPhase.Swapped, NewIncarnation = rebuilt }, token);
        var before = Hash(side);

        var outcome = await provider.GetRequiredService<IReplicaRebuild>().RebuildAsync(cluster.Database, original, token);

        await Assert.That(outcome is RebuildOutcome.Refused).IsTrue().Because("ambiguous history keeps recovery pending instead of discarding live entries");
        await Assert.That((await ledger.GetAsync(cluster.Database, token))?.Phase).IsEqualTo(RebuildPhase.Swapped);
        await Assert.That(Hash(side)).IsEqualTo(before);
        await Assert.That(await HeldStateAsync(receiver, "items", id)).IsNull().Because("the rejected salvage writer rolled back, including newer fields");
        await Assert.That(await provider.GetRequiredService<IReplicaCopyFaults>()
            .RefusedAsync(cluster.Database, rebuilt, token)).IsTrue();

        await receiver.StopAsync();
        await receiver.StartAsync();
        provider = receiver.Api.Server.Services;
        await Assert.That((await provider.GetRequiredService<IRebuildLedger>().GetAsync(cluster.Database, token))?.Phase)
            .IsEqualTo(RebuildPhase.Swapped);
        await Assert.That(await provider.GetRequiredService<IReplicaCopyFaults>()
            .RefusedAsync(cluster.Database, rebuilt, token)).IsTrue();
        await Assert.That(await provider.GetRequiredService<IReplicaRebuild>().RebuildAsync(cluster.Database, original, token)
            is RebuildOutcome.Refused).IsTrue();
        await Assert.That(Hash(side)).IsEqualTo(before).Because("restart and a refused retry preserve the original file and sidecars");
    }

    internal static async Task<DocumentState?> HeldStateAsync(PeerStreamTests.Machine machine, string collection, string id)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var read = await HeldTransactionAsync(machine);
        var (states, _) = await read.ReadBucketStatesAsync([SqliteStorageTransaction.Bucket(collection, id)], null,
            ChangeGroups.MaxDeltas, ChangeGroups.MaxBatchBytes, token);
        return states.SingleOrDefault(state => state.Collection == collection && state.Id == id)?.State;
    }

    internal static async Task<string?> ReadHeldAsync(PeerStreamTests.Machine machine, string id) =>
        (await HeldStateAsync(machine, "items", id))?.Materialize() is { } body ? Encoding.UTF8.GetString(body) : null;

    internal static async Task<SqliteStorageTransaction> HeldTransactionAsync(PeerStreamTests.Machine machine)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var store = (SqliteDatabaseStore)(await machine.Api.Server.Services.GetRequiredService<SqliteStorageEngine>()
            .OpenDatabaseAsync(machine.Cluster.Database, token))!;
        return await store.BeginRebuildTransactionAsync(token);
    }

    internal static async Task<string?> ReadOriginalAsync(PeerStreamTests.Machine machine, Ulid original, string id)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var storage = machine.Api.Server.Services.GetRequiredService<IRebuildStorage>();
        var side = storage.SidePath(machine.Cluster.Database, original);
        var before = Hash(side);
        var working = await storage.CopyForSalvageAsync(side, token);
        try
        {
            var rows = await storage.MeasureSideAsync(working, string.Empty, string.Empty, ChangeGroups.MaxDeltas, ChangeGroups.MaxBatchBytes, token);
            var states = await storage.ReadSideAsync(working, rows, token);
            return states.SingleOrDefault(state => state.Collection == "items" && state.Id == id)?.State.Materialize() is { } body
                ? Encoding.UTF8.GetString(body) : null;
        }
        finally
        {
            await storage.DiscardWorkingCopyAsync(working, CancellationToken.None);
            await Assert.That(Hash(side)).IsEqualTo(before);
        }
    }

    private static string Hash(string path) =>
        string.Join(",", new[] { string.Empty, "-wal", "-shm", "-journal" }.Where(sidecar => File.Exists(path + sidecar))
            .Select(sidecar => sidecar + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path + sidecar)))));

    private static EntryVersion At(ulong stamp) => new(new Hlc(stamp), ReplicaId.Of(99, 1, 0));
}
