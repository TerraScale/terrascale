using Bogus;
using TerraBase.Engine.Lattices;

namespace TerraBase.Fast.Lattices;

// Property tests of the lattice laws (TS-634 slice 1, docs/design/anna-core.md section 15). They call the library
// directly: TS-634 asks for lattice-law property tests, an exception to the integration-only rule that a human
// confirms on the pull request. Each run draws fresh seeds; a failure names its seed.
public sealed class LatticeLawTests
{
    private const int Seeds = 40;
    private const int Replicas = 3;
    private const int Steps = 30;

    [Test]
    public Task Max_lattices_obey_the_laws() =>
        AssertLawsAsync<MaxLattice>((random, state, _, _) => new MaxLattice(random.ULong(0, 1000)));

    [Test]
    public Task Lww_registers_obey_the_laws() =>
        AssertLawsAsync<LwwRegister<int>>((random, state, replica, _) =>
            new LwwRegister<int>(state.Stamp.Tick(random.Long(0, 50)), replica, random.Int()));

    [Test]
    public Task Grow_only_sets_obey_the_laws() =>
        AssertLawsAsync<SetLattice<int>>((random, state, _, _) => state.AddDelta(random.Int(0, 20)));

    [Test]
    public Task Map_lattices_obey_the_laws() =>
        AssertLawsAsync<MapLattice<int, MaxLattice>>((random, state, _, _) =>
            state.UpdateDelta(random.Int(0, 5), new MaxLattice(random.ULong(0, 1000))));

    [Test]
    public Task Document_states_obey_the_laws() =>
        AssertLawsAsync<DocumentState>((random, state, replica, _) =>
        {
            var (next, stamp) = DocumentOps.Apply(random, state, replica);
            return next.DeltaAt(stamp);
        });

    [Test]
    public async Task Hybrid_clocks_never_go_back()
    {
        foreach (var seed in NewSeeds())
        {
            var random = new Randomizer(seed);
            var clock = new Hlc(0);
            for (var step = 0; step < Steps; step++)
            {
                var now = random.Long(0, 100);
                var remote = new Hlc(random.ULong(0, 120UL << 16));
                var observe = random.Bool();
                var next = observe ? clock.Observe(remote, now) : clock.Tick(now);

                await Assert.That(next.Value).IsGreaterThan(observe ? Math.Max(clock.Value, remote.Value) : clock.Value).Because($"seed {seed}");
                await Assert.That(next.Milliseconds).IsGreaterThanOrEqualTo(now).Because($"seed {seed}");
                clock = next;
            }
        }
    }

    [Test]
    public async Task Replica_ids_pack_region_member_and_epoch()
    {
        foreach (var seed in NewSeeds())
        {
            var random = new Randomizer(seed);
            var (region, member, epoch) = ((ushort)random.Int(0, ushort.MaxValue), random.UInt(), (ushort)random.Int(0, ushort.MaxValue));
            var id = ReplicaId.Of(region, member, epoch);
            await Assert.That((id.Region, id.Member, id.Epoch)).IsEqualTo((region, member, epoch)).Because($"seed {seed}");
        }
    }

    // Replicas apply delta-mutations and receive each other's deltas in random order, with duplicates. The laws are
    // checked on the reachable states and deltas, then every replica receives every delta and all must converge
    // (delta-state CRDTs, Proposition 1). The mutator gets a random source, the replica's state, its id and the
    // states seen so far, and returns a delta.
    private static async Task AssertLawsAsync<T>(Func<Randomizer, T, ReplicaId, List<T>, T> mutate) where T : ILattice<T>
    {
        foreach (var seed in NewSeeds())
        {
            var random = new Randomizer(seed);
            var replicas = Enumerable.Repeat(T.Bottom, Replicas).ToArray();
            var deltas = new List<T>();
            var seen = new List<T> { T.Bottom };
            for (var step = 0; step < Steps; step++)
            {
                var replica = random.Int(0, Replicas - 1);
                if (deltas.Count == 0 || random.Bool())
                {
                    var delta = mutate(random, replicas[replica], R(replica + 1), seen);
                    deltas.Add(delta);
                    seen.Add(delta);
                    replicas[replica] = replicas[replica].Join(delta);
                }
                else
                {
                    replicas[replica] = replicas[replica].Join(random.ListItem(deltas));
                }

                seen.Add(replicas[replica]);
            }

            for (var check = 0; check < Steps; check++)
            {
                var (a, b, c) = (random.ListItem(seen), random.ListItem(seen), random.ListItem(seen));
                await Assert.That(a.Join(b).Join(c)).IsEqualTo(a.Join(b.Join(c))).Because($"associativity, seed {seed}");
                await Assert.That(a.Join(b)).IsEqualTo(b.Join(a)).Because($"commutativity, seed {seed}");
                await Assert.That(a.Join(a)).IsEqualTo(a).Because($"idempotence, seed {seed}");
                await Assert.That(a.Join(T.Bottom)).IsEqualTo(a).Because($"bottom, seed {seed}");
                await Assert.That(a.Join(a.Join(b))).IsEqualTo(a.Join(b)).Because($"inflation, seed {seed}");
            }

            var everything = deltas.Aggregate(T.Bottom, (state, delta) => state.Join(delta));
            for (var replica = 0; replica < Replicas; replica++)
            {
                var converged = random.Shuffle(deltas.Concat(deltas)).Aggregate(replicas[replica], (state, delta) => state.Join(delta));
                await Assert.That(converged).IsEqualTo(everything).Because($"convergence, seed {seed}");
            }
        }
    }

    private static IEnumerable<int> NewSeeds()
    {
        var first = Random.Shared.Next(int.MaxValue - Seeds);
        return Enumerable.Range(first, Seeds);
    }

    private static ReplicaId R(int member) => ReplicaId.Of(0, (uint)member, 0);
}
