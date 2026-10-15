using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ReplicaCluster = TerraBase.Fast.Replication.PeerStreamTests.Cluster;
using ReplicaMachine = TerraBase.Fast.Replication.PeerStreamTests.Machine;

namespace TerraBase.Fast.Replication;

// A collection drop reads every stored state it covers in one transaction: each read's admission must end with its document,
// so a tight foreground budget does not make a large drop fail, locally or as it replicates. The per-owner lease cap case
// is in the Slow A6LocalRebuildE2ETests.
public sealed class CollectionDropMemoryTests
{
    private const int MaxWrites = 1024;
    private const long Headroom = 256 * 1024;

    // With a unique constraint, each document's keys are also replaced, which reads the collection's definition.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_drop_under_a_tight_foreground_budget_releases_each_document_before_the_next(bool unique)
    {
        await using var cluster = await ReplicaCluster.StartAsync(cdc: true, clock: null, configure: (_, services) =>
        {
            var memory = services.Last(service => service.ServiceType == typeof(IMemoryBudget));
            services.AddSingleton<IMemoryBudget>(provider => new ForegroundBudget((IMemoryBudget)memory.ImplementationFactory!(provider)));
        }, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        // About 4 KiB is admitted per small document, so all of them together need several times the headroom.
        var ids = await SeedAsync(gru, MaxWrites, unique);
        await Assert.That(await sjc.UntilAsync(ids[^1], Body(ids[^1]))).IsTrue();

        // Control: with no headroom at all, the drop's state reads are refused, so admission is really in force.
        using (Hold(gru, headroom: 0))
        {
            _ = await Assert.ThrowsAsync<QosMemoryException>(async () => await gru.WriteAsync(async transaction =>
                _ = await transaction.DropCollectionAsync("items", CancellationToken.None)));
        }

        await Assert.That(await gru.ReadAsync(ids[0])).IsEqualTo(Body(ids[0]));
        using (Hold(gru, Headroom))
        using (Hold(sjc, Headroom))
        {
            await gru.WriteAsync(async transaction => await Assert.That(await transaction.DropCollectionAsync("items", CancellationToken.None)).IsTrue());
            await Assert.That(await gru.ReadAsync(ids[^1])).IsNull();
            // The peer applies the replicated drop while its own budget is still held.
            await Assert.That(await sjc.UntilAbsentAsync(ids[^1])).IsTrue();
            await Assert.That(await sjc.ReadAsync(ids[0])).IsNull();
        }

        await LaterWriteReplicatesAsync(gru, sjc);
    }

    private static string Body(string id) => $$"""{"email":"{{id}}"}""";

    private static async Task<string[]> SeedAsync(ReplicaMachine machine, int count, bool unique)
    {
        var ids = Enumerable.Range(0, count).Select(_ => EntityIds.New().ToString()).ToArray();
        await machine.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            if (unique)
            {
                await transaction.SetUniqueConstraintsAsync("items", [new UniqueConstraint("email", ["email"])], CancellationToken.None);
            }
        });
        foreach (var chunk in ids.Chunk(MaxWrites))
        {
            await machine.WriteAsync(async transaction =>
            {
                foreach (var id in chunk)
                {
                    _ = await transaction.PutAsync("items", id, Encoding.UTF8.GetBytes(Body(id)), CancellationToken.None);
                }
            });
        }

        return ids;
    }

    // The stream past the drop still applies and acknowledges: a later write reaches the peer.
    private static async Task LaterWriteReplicatesAsync(ReplicaMachine gru, ReplicaMachine sjc)
    {
        var later = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", later, Encoding.UTF8.GetBytes("""{"n":2}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(later, """{"n":2}""")).IsTrue();
        await Assert.That(await gru.UntilAckedAsync(gru.Cluster.Database, sjc.Node, await gru.HeadAsync(gru.Cluster.Database))).IsTrue();
    }

    // A separate cap cannot regain headroom when unrelated background reservations are released.
    private static IDisposable Hold(ReplicaMachine machine, long headroom) =>
        ((ForegroundBudget)machine.Api.Server.Services.GetRequiredService<IMemoryBudget>()).Hold(headroom);

    private sealed class ForegroundBudget(IMemoryBudget inner) : IMemoryBudget
    {
        private Limit? _limit;

        public IDisposable Hold(long headroom)
        {
            var limit = new Limit(this, headroom);
            if (Interlocked.CompareExchange(ref _limit, limit, null) is not null)
            {
                limit.Dispose();
                throw new InvalidOperationException("the foreground budget is already held");
            }
            return limit;
        }

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => TryReserve(lane, bytes, QosContext.Current);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant)
        {
            if (lane != MemoryLane.Foreground || Volatile.Read(ref _limit) is not { } limit)
            {
                return inner.TryReserve(lane, bytes, tenant);
            }

            if (limit.Budget.TryReserve(lane, bytes, tenant: null) is not { } capped)
            {
                return null;
            }
            if (inner.TryReserve(lane, bytes, tenant) is not { } admitted)
            {
                capped.Dispose();
                return null;
            }
            return new Reservation(capped, admitted);
        }

        private sealed class Limit : IDisposable
        {
            private readonly ForegroundBudget _owner;
            private readonly Meter _meter = new("CollectionDropMemoryTests");

            public Limit(ForegroundBudget owner, long headroom)
            {
                _owner = owner;
                Budget = new MemoryBudget(headroom, _meter);
            }

            public MemoryBudget Budget { get; }

            public void Dispose()
            {
                _ = Interlocked.CompareExchange(ref _owner._limit, null, this);
                _meter.Dispose();
            }
        }

        private sealed class Reservation(IMemoryReservation capped, IMemoryReservation admitted) : IMemoryReservation
        {
            public long Bytes => admitted.Bytes;

            public void Dispose()
            {
                admitted.Dispose();
                capped.Dispose();
            }
        }
    }
}
