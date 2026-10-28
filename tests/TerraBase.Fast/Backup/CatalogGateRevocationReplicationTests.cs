using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

/// <summary>
/// The last region of a rolling upgrade restarts after the others anchored new revocation markers it has not received yet.
/// On a large catalog over the WAN the revocation snapshot held the writer for minutes and the region never became ready. An
/// attempt is now cut off after its budget, rolled back and awaited before the gate backs off, and the peers' streams deliver
/// those markers meanwhile, so the gate opens anyway, only once its catalog covers them.
/// </summary>
public sealed class CatalogGateRevocationReplicationTests
{
    private const string Probe = "revocation_replication_probe";

    [Test]
    public async Task A_region_whose_revocation_snapshot_hangs_opens_by_its_peers_streams()
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

        // As gru and sjc did on their first anchor after the upgrade: markers fra misses while it is down.
        await fra.StopAsync();
        var cuts = new List<(ReplicaId Origin, long Cut)>();
        foreach (var witness in (RestartingMachine[])[gru, sjc])
        {
            await Assert.That(await witness.Gate.TryAnchorAsync(token)).IsTrue();
            cuts.Add(await CutAsync(witness, token));
        }

        // fra's real importer reads every page, the terminal one included, then the peer's stream stalls before its end: the
        // whole copy is written under its one durable transaction, which never commits, as a large one over the WAN.
        var imports = new StallingImporter();
        await fra.StartAsync(services => services.AddSingleton<IReplicaSnapshotImporter>(provider =>
            imports.Wrap(ActivatorUtilities.CreateInstance<TursoReplicaSnapshotImporter>(provider))));

        // The attempt is cut off after its budget and awaited: every import ended by cancellation before its commit (the
        // importer's last step, which also records the applied vector), so its states and vector rolled back together.
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(imports.Ended > 0), timeout: CatalogGate.SnapshotBudget * 3);
        await Assert.That(imports.Ended).IsEqualTo(imports.Started);
        await Assert.That(imports.Outcomes.All(outcome => outcome == typeof(OperationCanceledException))).IsTrue();

        // The writer is free again before the next attempt.
        var catalog = await fra.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        var started = Stopwatch.GetTimestamp();
        await using (var write = await catalog.BeginTransactionAsync(write: true, token))
        {
            _ = await write.PutAsync(Probe, "after-cut-off", Encoding.UTF8.GetBytes("""{"free":true}"""), token);
            _ = await write.CommitAsync(token);
        }

        await Assert.That(Stopwatch.GetElapsedTime(started)).IsLessThan(TimeSpan.FromSeconds(2));

        // Their streams reconnect within their backoff (at most 30 s) while the next snapshot attempt waits.
        try
        {
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(fra.Gate.IsOpen), timeout: TimeSpan.FromSeconds(60));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Assert.Fail($"fra's catalog gate stayed closed, waiting for: {string.Join("; ", fra.Gate.Waiting)}");
        }

        // Open only on a catalog that covers every anchored revocation.
        await using var read = await catalog.BeginTransactionAsync(write: false, token);
        var applied = await read.GetAppliedAsync(token);
        foreach (var (origin, cut) in cuts)
        {
            await Assert.That(applied.TryGetValue(origin, out var held) && held.Value >= unchecked((ulong)cut)).IsTrue();
        }
    }

    // The revocation marker a witness just anchored: its replica and the marker's commit.
    private static async Task<(ReplicaId, long)> CutAsync(RestartingMachine witness, CancellationToken token)
    {
        var services = witness.Api.Server.Services;
        var catalog = await services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var read = await catalog.BeginTransactionAsync(write: false, token);
        var machine = services.GetRequiredService<TerraBaseOptions>().MachineId;
        return (services.GetRequiredService<LocalReplica>().Id, (await read.GetVersionAsync(CatalogGate.Revocations, machine, token))!.Value.Version);
    }

    // Wraps the real importer: its pages stall after the last one until the import is cancelled. Records how each ended.
    private sealed class StallingImporter
    {
        private readonly ConcurrentQueue<Ulid> _started = new();
        private readonly ConcurrentQueue<Type> _outcomes = new();

        public int Started => _started.Count;

        public int Ended => _outcomes.Count;

        public IReadOnlyCollection<Type> Outcomes => [.. _outcomes];

        public IReplicaSnapshotImporter Wrap(IReplicaSnapshotImporter inner) => new Importer(this, inner);

        private sealed class Importer(StallingImporter owner, IReplicaSnapshotImporter inner) : IReplicaSnapshotImporter
        {
            public async Task<bool> ImportAsync(TenantContext database, Ulid incarnation, IAsyncEnumerable<ReplicaSnapshotPage> pages,
                IReadOnlySet<ulong> origins, CancellationToken cancellationToken, TimeSpan? holdBudget = null)
            {
                owner._started.Enqueue(incarnation);
                var outcome = typeof(bool);
                try
                {
                    return await inner.ImportAsync(database, incarnation, StallAsync(pages, cancellationToken), origins, cancellationToken, holdBudget);
                }
                catch (Exception error)
                {
                    outcome = error is OperationCanceledException ? typeof(OperationCanceledException) : error.GetType();
                    throw;
                }
                finally
                {
                    owner._outcomes.Enqueue(outcome);
                }
            }

            private static async IAsyncEnumerable<ReplicaSnapshotPage> StallAsync(IAsyncEnumerable<ReplicaSnapshotPage> pages,
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await foreach (var page in pages.WithCancellation(cancellationToken))
                {
                    yield return page;
                }

                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }
}
