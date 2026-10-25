using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

/// <summary>
/// fra, 2026-10-25: a restarted region missed one revocation cut, sjc's. gru held sjc's catalog changes only up to an older
/// stamp, and the gate tried gru first on every pass: a failed import from it ended the pass before sjc was asked. The
/// gate now imports from the machine whose anchor names the missing cut, and one failing witness does not stop the others.
/// </summary>
public sealed class CatalogGateRevocationWriterTests
{
    [Test]
    public async Task A_missing_cut_is_imported_from_its_writer_when_another_witness_cannot_export()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var bucket = new FakeS3();
        var members = new FakeMemberStore();
        await using var gru = new RestartingMachine("gru", bucket, members);
        await using var sjc = new RestartingMachine("sjc", bucket, members);
        await using var fra = new RestartingMachine("fra", bucket, members);
        foreach (var machine in (RestartingMachine[])[gru, sjc, fra])
        {
            await machine.StartAsync();
        }

        foreach (var machine in (RestartingMachine[])[gru, sjc, fra])
        {
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(machine.Gate.IsOpen), timeout: TimeSpan.FromSeconds(30));
        }

        // The witness the gate tries first by name cannot export a snapshot; the other one writes the only new cut.
        var (relay, writer) = string.CompareOrdinal(Name(gru), Name(sjc)) < 0 ? (gru, sjc) : (sjc, gru);
        await relay.StopAsync();
        await relay.StartAsync(services => services.AddSingleton<IReplicaSnapshotSource>(new FailingSource()));
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(relay.Gate.IsOpen), timeout: TimeSpan.FromSeconds(30));

        await fra.StopAsync();
        await Assert.That(await writer.Gate.TryAnchorAsync(token)).IsTrue();

        // fra's catalog streams apply nothing: only a revocation snapshot can cover the writer's cut.
        await fra.StartAsync(services => services.AddSingleton<IChangeGroupSink>(provider =>
            new HeldCatalogSink(ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider))));
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(fra.Gate.IsOpen), timeout: TimeSpan.FromSeconds(60));
    }

    private static string Name(RestartingMachine machine) => machine.Api.Server.Services.GetRequiredService<TerraBaseOptions>().MachineId;

    private sealed class FailingSource : IReplicaSnapshotSource
    {
        public IAsyncEnumerable<ReplicaSnapshotPage> SnapshotAsync(TenantContext database, Ulid incarnation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("this witness cannot export a snapshot");
    }

    private sealed class HeldCatalogSink(IChangeGroupSink inner) : IChangeGroupSink
    {
        public async ValueTask<Hlc?> ApplyAsync(
            TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, Hlc expected, CancellationToken cancellationToken, ApplyProgress? progress = null)
        {
            if (database == ChangeGroups.System)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await inner.ApplyAsync(database, origin, groups, expected, cancellationToken, progress);
        }

        public ValueTask<int?> RepairAsync(TenantContext database, Ulid incarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken) =>
            inner.RepairAsync(database, incarnation, states, cancellationToken);

        public async ValueTask<Ulid?> RecordFrontierAsync(
            TenantContext database, ReplicaId origin, Hlc frontier, Hlc expected, Ulid? incarnation, CancellationToken cancellationToken)
        {
            if (database == ChangeGroups.System)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await inner.RecordFrontierAsync(database, origin, frontier, expected, incarnation, cancellationToken);
        }
    }
}
