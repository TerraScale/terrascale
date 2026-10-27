using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class CorruptChangeLogMaintenanceTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Retention_marks_decoder_damage_before_disposal_and_cannot_commit(bool missingImage)
    {
        using var directory = new TempDirectory();
        var time = new FakeTimeProvider();
        await using var api = await StartAsync(directory.Root, time);
        var (database, store, before, peer) = await DamageAsync(api, time, missingImage);
        var services = api.Server.Services;
        var ledger = services.GetRequiredService<IRebuildLedger>();
        var transaction = (TursoStorageTransaction)await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await using (transaction)
        {
            // The retention scan catches its decoder error internally, without an outer source read.
            _ = await transaction.TrimAsync(store.BackupAnchor, time.GetUtcNow(),
                services.GetRequiredService<TursoStorageEngine>().ReplicaId, new HashSet<long>(), CancellationToken.None);
            await Assert.That(store.CopyFault).IsNotNull();
            await Assert.That(await ledger.GetAsync(database, CancellationToken.None)).IsNull()
                .Because("the held transaction records its fault only during disposal");
            _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
                await transaction.CommitAsync(CancellationToken.None));
        }

        await Assert.That((await ledger.GetAsync(database, CancellationToken.None))?.OldIncarnation).IsEqualTo(store.Incarnation);
        await using (var read = (TursoStorageTransaction)await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That((await read.GetAckedAsync(peer, store.Incarnation, CancellationToken.None))?.Acked).IsEqualTo(before);
        }
        await AssertRefusedAsync(api, database, store, peer, before);
    }

    [Test]
    public async Task Concrete_source_retention_alone_records_a_fault_that_survives_clean_restart()
    {
        using var directory = new TempDirectory();
        TenantContext database;
        Ulid incarnation;
        long before;
        ReplicaId peer;
        await using (var api = await StartAsync(directory.Root, new FakeTimeProvider()))
        {
            (database, var store, before, peer) = await DamageAsync(api,
                api.Server.Services.GetRequiredService<TimeProvider>(), missingImage: false);
            incarnation = store.Incarnation;
            // This is the same concrete pass the hosted worker runs; no wrapped group read detects the damage.
            _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
                await api.Server.Services.GetRequiredService<TursoChangeGroupSource>().TrimAsync(store, CancellationToken.None));
            await Assert.That((await api.Server.Services.GetRequiredService<IRebuildLedger>()
                .GetAsync(database, CancellationToken.None))?.OldIncarnation).IsEqualTo(incarnation);
            await AssertRefusedAsync(api, database, store, peer, before);
        }

        await using var restarted = await StartAsync(directory.Root, new FakeTimeProvider());
        var services = restarted.Server.Services;
        var copy = (TursoDatabaseStore)(await services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the damaged copy is missing"));
        await Assert.That(copy.Incarnation).IsEqualTo(incarnation);
        await Assert.That((await services.GetRequiredService<IRebuildLedger>()
            .GetAsync(database, CancellationToken.None))?.OldIncarnation).IsEqualTo(incarnation);
        await AssertRefusedAsync(restarted, database, copy, peer, before);
    }

    private static Task<ManagementApi> StartAsync(string root, TimeProvider time) =>
        ManagementApi.StartAsync(settings => settings["data-directory"] = root,
            services => services.AddSingleton<IReplicaRebuild, RefusedRebuild>(), time: time);

    private static async Task<(TenantContext Database, TursoDatabaseStore Store, long Before, ReplicaId Peer)> DamageAsync(
        ManagementApi api, TimeProvider time, bool missingImage)
    {
        var managed = await api.CreateManagedDatabaseAsync();
        var services = api.Server.Services;
        var database = await services.GetRequiredService<ITenantDirectory>()
            .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
            is TenantContext found ? found : throw new InvalidOperationException("the test database is not registered");
        var store = (TursoDatabaseStore)(await services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the test copy is missing"));
        await using (var setup = await store.BeginDurableTransactionAsync(CancellationToken.None))
        {
            _ = await setup.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await setup.CommitAsync(CancellationToken.None);
        }

        var before = await services.GetRequiredService<TursoChangeGroupSource>().HeadAsync(database, store.Incarnation, CancellationToken.None)
            ?? throw new InvalidOperationException("the healthy source head was refused");
        var peer = ReplicaId.Of(99, 1, 0);
        await using (var ack = (TursoStorageTransaction)await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            // Seed bookkeeping directly: an ACK through the source would also read its tail gauge.
            _ = await ack.RecordAckedAsync(peer, store.Incarnation, before, default, store.BackupAnchor, time.GetUtcNow(), members: null, CancellationToken.None);
            _ = await ack.CommitAsync(CancellationToken.None);
        }
        await using (var write = await store.BeginDurableTransactionAsync(CancellationToken.None))
        {
            _ = await write.PutAsync("items", "held", JsonSerializer.SerializeToUtf8Bytes(new { n = new Faker().Random.Int(1, 1000) }), CancellationToken.None);
            _ = await write.CommitAsync(CancellationToken.None);
        }

        var damaged = await store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = missingImage
                ? "UPDATE turso_cdc SET after = NULL WHERE change_id = (SELECT min(change_id) FROM turso_cdc WHERE change_id > $before AND table_name = '_tb_state');"
                : "UPDATE turso_cdc SET change_type = 7 WHERE change_id = (SELECT min(change_id) FROM turso_cdc WHERE change_id > $before AND table_name = '_tb_state');";
            command.Parameters.AddWithValue("$before", before);
            return command.ExecuteNonQuery();
        }, "a maintenance test damaging a CDC row", CancellationToken.None);
        await Assert.That(damaged).IsEqualTo(1);
        await Assert.That(store.CopyFault).IsNull();
        await Assert.That(await services.GetRequiredService<IRebuildLedger>().GetAsync(database, CancellationToken.None)).IsNull();
        return (database, store, before, peer);
    }

    private static async Task AssertRefusedAsync(ManagementApi api, TenantContext database, TursoDatabaseStore store, ReplicaId peer, long before)
    {
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
        {
            await using var write = await store.BeginDurableTransactionAsync(CancellationToken.None);
        });
        await Assert.That(await api.Server.Services.GetRequiredService<IChangeGroupSource>()
            .AckAsync(database, store.Incarnation, peer, before, default, CancellationToken.None)).IsFalse();
    }

    private sealed class RefusedRebuild : IReplicaRebuild
    {
        public ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RebuildOutcome>(new RebuildOutcome.Refused("a maintenance test preserves the suspect copy"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("terrabase-corrupt-cdc-maintenance-").FullName;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
