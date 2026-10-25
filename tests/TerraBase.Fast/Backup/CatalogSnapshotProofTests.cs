using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class CatalogSnapshotProofTests
{
    [Test]
    public async Task A_snapshot_never_exports_received_coverage_as_its_own_committed_cut()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync();
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        await using (var write = (TursoStorageTransaction)await store.BeginDurableTransactionAsync(token))
        {
            await Assert.That((await write.GetLocalSnapshotCutAsync(engine.ReplicaId, token)).Value).IsEqualTo(0UL);
            await write.RecordAppliedAsync(engine.ReplicaId, new Hlc(123), token);
            _ = await write.CommitAsync(token);
        }
        await foreach (var page in api.Server.Services.GetRequiredService<IReplicaSnapshotSource>()
            .SnapshotAsync(ChangeGroups.System, store.Incarnation, token))
        {
            if (page.Applied is { } applied)
            {
                await Assert.That(applied.ContainsKey(engine.ReplicaId)).IsFalse();
            }
        }
    }

    // TS-1012: the target forgets a live catalog state the source deleted and collected only when the source's terminal cuts
    // cover every author of that state, and the stream ends cleanly. 0 is the untampered snapshot.
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task A_live_catalog_survivor_requires_its_author_cut_and_clean_EOF(int failure)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var source = await ManagementApi.StartAsync();
        await using var target = await ManagementApi.StartAsync();
        var ghost = await source.CreateOrganizationAsync("collected at its author");
        var origin = source.Server.Services.GetRequiredService<LocalReplica>().Id;
        var sourceStore = (TursoDatabaseStore)await source.Server.Services.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(token);
        var targetStore = await target.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        var exporter = source.Server.Services.GetRequiredService<IReplicaSnapshotSource>();
        var importer = target.Server.Services.GetRequiredService<IReplicaSnapshotImporter>();
        var origins = new HashSet<ulong> { origin.Value };
        await Assert.That(await importer.ImportAsync(ChangeGroups.System, targetStore.Incarnation,
            exporter.SnapshotAsync(ChangeGroups.System, sourceStore.Incarnation, token), origins, token)).IsTrue();
        // The source applies the target's own catalog: only the ghost's author cut decides the import below.
        var self = target.Server.Services.GetRequiredService<LocalReplica>().Id;
        await Assert.That(await source.Server.Services.GetRequiredService<IReplicaSnapshotImporter>().ImportAsync(ChangeGroups.System,
            sourceStore.Incarnation, target.Server.Services.GetRequiredService<IReplicaSnapshotSource>().SnapshotAsync(ChangeGroups.System,
                targetStore.Incarnation, token), new HashSet<ulong> { self.Value }, token)).IsTrue();
        await using (var write = (TursoStorageTransaction)await sourceStore.BeginDurableTransactionAsync(token))
        {
            _ = await write.DeleteAsync("organizations", ghost, token);
            _ = await write.CommitAsync(token);
        }
        await using (var collect = (TursoStorageTransaction)await sourceStore.BeginDurableTransactionAsync(token))
        {
            var deleted = (await collect.GetVersionAsync("organizations", ghost, token))!.Value.Version;
            await Assert.That(await collect.CollectAsync(new Hlc(unchecked((ulong)deleted) + 1), token)).IsGreaterThan(0);
            _ = await collect.CommitAsync(token);
        }
        if (failure == 3)
        {
            _ = await Assert.ThrowsAsync<IOException>(ImportAsync);
        }
        else
        {
            await Assert.That(await ImportAsync()).IsEqualTo(failure == 0);
        }
        await using var read = await targetStore.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetAsync("organizations", ghost, token) is { Deleted: false }).IsEqualTo(failure != 0);

        Task<bool> ImportAsync()
        {
            return importer.ImportAsync(ChangeGroups.System, targetStore.Incarnation, Pages(token), origins, token);
        }

        async IAsyncEnumerable<ReplicaSnapshotPage> Pages([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var page in exporter.SnapshotAsync(ChangeGroups.System, sourceStore.Incarnation, cancellationToken))
            {
                yield return page.Applied is not { } applied ? page : failure switch
                {
                    1 => page with { Applied = applied.Where(cut => cut.Key != origin).ToDictionary() },
                    2 => page with { Applied = applied.ToDictionary(cut => cut.Key, cut => cut.Key == origin ? new Hlc(1) : cut.Value) },
                    _ => page,
                };
            }
            if (failure == 3)
            {
                throw new IOException("failed trailers after the terminal proof");
            }
        }
    }
}
