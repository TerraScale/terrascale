using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class CatalogGateCollectedRevocationTests
{
    // TS-1012 (prod, 2026-10-25): sjc deleted and collected catalog rows that gru and fra still held, most of them written by
    // gru or fra, not sjc. byPeer has the stale region write the revoked row and the writer delete it.
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task A_collected_revocation_recovers_a_restarted_or_stale_seeded_catalog(bool fresh, bool byPeer)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var bucket = new FakeS3();
        var members = new FakeMemberStore();
        await using var writer = new RestartingMachine("sjc", bucket, members);
        await using var stale = new RestartingMachine("gru", bucket, members);
        await using var original = new RestartingMachine("fra", bucket, members);
        foreach (var machine in (RestartingMachine[])[writer, stale, original])
        {
            await machine.StartAsync(HoldStreams);
            await OpenAsync(machine);
        }

        foreach (var peer in (RestartingMachine[])[stale, original])
        {
            await Assert.That(await writer.Api.Server.Services.GetRequiredService<ReplicaSnapshots>().PullAsync(ChangeGroups.System,
                Peer(peer),
                new HashSet<ulong> { peer.Api.Server.Services.GetRequiredService<LocalReplica>().Id.Value }, token)).IsTrue();
        }
        var revoked = await (byPeer ? stale : writer).Api.CreateOrganizationAsync("deleted before the revocation cut");
        if (byPeer)
        {
            // The writer applies the stale region's catalog, the row included, before deleting it.
            await Assert.That(await writer.Api.Server.Services.GetRequiredService<ReplicaSnapshots>().PullAsync(ChangeGroups.System,
                Peer(stale),
                new HashSet<ulong> { stale.Api.Server.Services.GetRequiredService<LocalReplica>().Id.Value }, token)).IsTrue();
        }

        await PullAsync(stale, writer, token);
        await PullAsync(original, writer, token);
        var origin = writer.Api.Server.Services.GetRequiredService<LocalReplica>().Id;
        var held = await AppliedAsync(original, origin, token);
        await stale.StopAsync();
        await original.StopAsync();

        var source = (TursoDatabaseStore)await writer.Api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(token);
        Hlc floor;
        await using (var write = (TursoStorageTransaction)await source.BeginDurableTransactionAsync(token))
        {
            await Assert.That(await write.DeleteAsync("organizations", revoked, token)).IsTrue();
            var deleted = (await write.GetVersionAsync("organizations", revoked, token))!.Value.Version;
            floor = new Hlc(unchecked((ulong)deleted) + 1);
            _ = await write.CommitAsync(token);
        }

        // Faithful post-incident state, not a claim that normal GC can reach this horizon with a stalled listed peer.
        // Use the real collector with an explicit cut, as ReplicaSnapshotTests does; preserve real snapshot/peer/gate IO.
        await using (var collect = (TursoStorageTransaction)await source.BeginDurableTransactionAsync(token))
        {
            var now = writer.Api.Server.Services.GetRequiredService<LocalReplica>().Clock.Tick();
            await Assert.That((await collect.GetHorizonAsync(now, token)).Value).IsLessThan(floor.Value);
            await Assert.That(await collect.CollectAsync(floor, token)).IsGreaterThan(0);
            _ = await collect.CommitAsync(token);
        }
        await Assert.That(await writer.Gate.TryAnchorAsync(token)).IsTrue();
        await using (var read = await source.BeginTransactionAsync(write: false, token))
        {
            await Assert.That(await read.GetVersionAsync("organizations", revoked, token)).IsNull();
        }

        // The stale holder remains available and trusted by its own anchor, but cannot acquire the new writer cut.
        await stale.StartAsync(services =>
        {
            HoldStreams(services);
            services.AddSingleton<IReplicaSnapshotImporter>(new RefusingImporter());
        });
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(stale.Gate.Trusted), timeout: TimeSpan.FromSeconds(10));
        await Assert.That(stale.Gate.IsOpen).IsFalse();
        await Assert.That(await LiveAsync(stale, revoked, token)).IsTrue();

        await using var newcomer = new RestartingMachine("fra", bucket, members);
        var receiver = fresh ? newcomer : original;
        var imports = new ImportObservation(revoked, origin, held);
        await receiver.StartAsync(services =>
        {
            HoldStreams(services);
            services.AddSingleton<IReplicaSnapshotImporter>(provider =>
                new ObservingImporter(ActivatorUtilities.CreateInstance<TursoReplicaSnapshotImporter>(provider),
                    provider.GetRequiredService<IStorageEngine>(), imports));
        });
        // VerifyPeerAsync's real anti-entropy pull seeds the fresh machine from stale before any snapshot import.
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(imports.SawUncoveredSurvivor), timeout: TimeSpan.FromSeconds(10));
        await Assert.That(imports.SawUncoveredSurvivor).IsTrue();
        await Assert.That(await AppliedAsync(receiver, origin, token)).IsGreaterThanOrEqualTo(held);
        await OpenAsync(receiver);
        await Assert.That(imports.Imported).IsTrue();
        await Assert.That(await LiveAsync(receiver, revoked, token)).IsFalse();
        await Assert.That(await AppliedAsync(receiver, origin, token)).IsGreaterThan(held);
        await PullAsync(receiver, stale, token);
        await Assert.That(await LiveAsync(receiver, revoked, token)).IsFalse()
            .Because("the inherited floor rejects the stale holder's revoked survivor after admission too");
    }

    private static async Task OpenAsync(RestartingMachine machine) =>
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(machine.Gate.IsOpen), timeout: TimeSpan.FromSeconds(25));

    private static ClusterMember Peer(RestartingMachine machine) =>
        machine.Api.Server.Services.GetRequiredService<IMembership>().Self with { PeerAddress = machine.Api.Server.PeerAddress!.ToString() };

    private static Task<int?> PullAsync(RestartingMachine receiver, RestartingMachine source, CancellationToken token) =>
        receiver.Api.Server.Services.GetRequiredService<AntiEntropy>().PullAsync(ChangeGroups.System,
            Peer(source), token);

    private static async Task<bool> LiveAsync(RestartingMachine machine, string id, CancellationToken token)
    {
        var store = await machine.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var read = await store.BeginTransactionAsync(write: false, token);
        return await read.GetAsync("organizations", id, token) is { Deleted: false };
    }

    private static async Task<ulong> AppliedAsync(RestartingMachine machine, ReplicaId origin, CancellationToken token)
    {
        var store = await machine.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var read = await store.BeginTransactionAsync(write: false, token);
        return (await read.GetAppliedAsync(token)).GetValueOrDefault(origin).Value;
    }

    private static void HoldStreams(IServiceCollection services) => services.AddSingleton<IChangeGroupSink>(provider =>
        new HeldSink(ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider)));

    private sealed class HeldSink(IChangeGroupSink inner) : IChangeGroupSink
    {
        public async ValueTask<Hlc?> ApplyAsync(TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups,
            Hlc expected, CancellationToken cancellationToken, ApplyProgress? progress = null)
        {
            if (database == ChangeGroups.System)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return await inner.ApplyAsync(database, origin, groups, expected, cancellationToken, progress);
        }

        public ValueTask<int?> RepairAsync(TenantContext database, Ulid incarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken) =>
            inner.RepairAsync(database, incarnation, states, cancellationToken);

        public async ValueTask<Ulid?> RecordFrontierAsync(TenantContext database, ReplicaId origin, Hlc frontier, Hlc expected,
            Ulid? incarnation, CancellationToken cancellationToken)
        {
            if (database == ChangeGroups.System)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return await inner.RecordFrontierAsync(database, origin, frontier, expected, incarnation, cancellationToken);
        }
    }

    private sealed class RefusingImporter : IReplicaSnapshotImporter
    {
        public Task<bool> ImportAsync(TenantContext database, Ulid incarnation, IAsyncEnumerable<ReplicaSnapshotPage> pages,
            IReadOnlySet<ulong> origins, CancellationToken cancellationToken, TimeSpan? holdBudget = null) => Task.FromResult(false);
    }

    private sealed class ImportObservation(string id, ReplicaId origin, ulong held)
    {
        public string Id { get; } = id;
        public ReplicaId Origin { get; } = origin;
        public ulong Held { get; } = held;
        public volatile bool SawUncoveredSurvivor;
        public volatile bool Imported;
    }

    private sealed class ObservingImporter(IReplicaSnapshotImporter inner, IStorageEngine engine, ImportObservation observation) : IReplicaSnapshotImporter
    {
        public async Task<bool> ImportAsync(TenantContext database, Ulid incarnation, IAsyncEnumerable<ReplicaSnapshotPage> pages,
            IReadOnlySet<ulong> origins, CancellationToken cancellationToken, TimeSpan? holdBudget = null)
        {
            // The gate has completed all peer verification pulls before it invokes the importer.
            var store = await engine.OpenSystemDatabaseAsync(cancellationToken);
            await using (var read = await store.BeginTransactionAsync(write: false, cancellationToken))
            {
                observation.SawUncoveredSurvivor |= await read.GetAsync("organizations", observation.Id, cancellationToken) is { Deleted: false }
                    && (await read.GetAppliedAsync(cancellationToken)).GetValueOrDefault(observation.Origin).Value <= observation.Held;
            }
            var imported = await inner.ImportAsync(database, incarnation, pages, origins, cancellationToken, holdBudget);
            observation.Imported |= imported;
            Console.WriteLine($"revocation snapshot imported={imported}");
            return imported;
        }
    }
}
