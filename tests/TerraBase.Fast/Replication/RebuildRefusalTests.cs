using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class RebuildRefusalTests
{
    [Test]
    public async Task Production_recovery_preserves_the_side_copy_and_refuses_writes_until_certified_refill_is_available()
    {
        await using var api = await ManagementApi.StartAsync();
        var managed = await api.CreateManagedDatabaseAsync();
        var services = api.Server.Services;
        var database = await services.GetRequiredService<ITenantDirectory>()
            .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
            is TenantContext found ? found : throw new InvalidOperationException("the test database is not registered");
        var engine = services.GetRequiredService<IStorageEngine>();
        var store = await engine.OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the test database is missing");
        await using (var setup = await store.BeginDurableTransactionAsync(CancellationToken.None))
        {
            _ = await setup.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await setup.PutAsync("items", "kept", "{\"n\":1}"u8.ToArray(), CancellationToken.None);
            _ = await setup.CommitAsync(CancellationToken.None);
        }

        var old = store.Incarnation;
        await services.GetRequiredService<IReplicaCopyFaults>().RecordAsync(database, old, "a test copy fault", CancellationToken.None);
        var ledger = services.GetRequiredService<IRebuildLedger>();
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await ledger.GetAsync(database, token) is { Phase: RebuildPhase.Swapped, NewIncarnation: not null }, TimeSpan.FromSeconds(60));
        var record = await ledger.GetAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the rebuild record is missing");
        var rebuilt = record.NewIncarnation!.Value;
        await Assert.That(rebuilt).IsNotEqualTo(old);
        await Assert.That(services.GetRequiredService<IRebuildStorage>().SideExists(record.SidePath)).IsTrue();
        var certification = services.GetRequiredService<IRebuildCertification>();
        await Assert.That(await certification.RefillAsync(database, rebuilt, CancellationToken.None)).IsFalse();
        await Assert.That(await certification.CompleteAsync(database, old, rebuilt, CancellationToken.None)).IsFalse();
        var copy = await engine.OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the replacement is missing");
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
        {
            await using var refused = await copy.BeginDurableTransactionAsync(CancellationToken.None);
        });
        var groups = services.GetRequiredService<IChangeGroupSource>();
        await Assert.That(await groups.AckAsync(database, copy.Incarnation, ReplicaId.Of(1, 7, 0), 1, default, CancellationToken.None)).IsFalse()
            .Because("a refused replacement cannot persist a successful source ACK");
        using var channel = PeerChannel.Create(api.Server.BaseAddress, PeerTls.Credential);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        await Assert.That((await client.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(database, copy.Incarnation) },
            deadline: DateTime.UtcNow.AddSeconds(60))).Held).IsFalse();
        await Assert.That((await ledger.GetAsync(database, CancellationToken.None))?.Phase).IsEqualTo(RebuildPhase.Swapped);
        await Assert.That(services.GetRequiredService<IRebuildStorage>().SideExists(record.SidePath)).IsTrue();
    }
}
