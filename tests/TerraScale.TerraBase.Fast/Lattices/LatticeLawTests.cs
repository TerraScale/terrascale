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
    public Task Grow_only_counters_obey_the_laws() =>
        AssertLawsAsync<GCounter>((random, state, replica, _) => state.IncrementDelta(replica, random.ULong(1, 10)));

    [Test]
    public Task Causal_contexts_obey_the_laws() =>
        AssertLawsAsync<CausalContext>((random, state, replica, _) =>
            CausalContext.Of([new Dot(replica, state.Max(replica) + 1), new Dot(R(random.Int(1, Replicas)), random.ULong(1, 12))]));

    [Test]
    public Task Multi_value_registers_obey_the_laws() =>
        AssertLawsAsync<MvRegister<int>>((random, state, replica, seen) =>
            random.Bool() ? state.WriteDelta(replica, random.Int()) : state.WriteDelta(replica, random.Int(), random.ListItem(seen).Context));

    [Test]
    public Task Add_wins_sets_obey_the_laws() =>
        AssertLawsAsync<AwSet<int>>((random, state, replica, _) =>
            random.Bool() ? state.AddDelta(replica, random.Int(0, 6)) : state.RemoveDelta(random.Int(0, 6)));

    [Test]
    public async Task Causal_contexts_hold_exactly_their_dots_whatever_the_order()
    {
        foreach (var seed in NewSeeds())
        {
            var random = new Randomizer(seed);
            var dots = Enumerable.Range(0, random.Int(0, 30)).Select(_ => new Dot(R(random.Int(1, Replicas)), random.ULong(1, 10))).ToHashSet();
            var context = CausalContext.Of(random.Shuffle(dots));
            var oneByOne = random.Shuffle(dots).Aggregate(new CausalContext(), (state, dot) => state.Add(dot));

            await Assert.That(oneByOne).IsEqualTo(context).Because($"seed {seed}");
            foreach (var replica in Enumerable.Range(1, Replicas).Select(R))
            {
                for (var counter = 1UL; counter <= 11; counter++)
                {
                    await Assert.That(context.Contains(new Dot(replica, counter))).IsEqualTo(dots.Contains(new Dot(replica, counter))).Because($"seed {seed}");
                }

                var max = dots.Where(dot => dot.Replica == replica).Select(dot => dot.Counter).DefaultIfEmpty(0UL).Max();
                await Assert.That(context.Max(replica)).IsEqualTo(max).Because($"seed {seed}");
            }
        }
    }

    [Test]
    public async Task A_register_keeps_concurrent_writes_and_replaces_observed_ones()
    {
        var a = new MvRegister<string>();
        var b = new MvRegister<string>();
        var writeA = a.WriteDelta(R(1), "a");
        var writeB = b.WriteDelta(R(2), "b");
        a = a.Join(writeA);
        b = b.Join(writeB);

        var both = a.Join(b);
        await Assert.That(both.Values.Values.Order()).IsEquivalentTo(["a", "b"]);

        // A causal write that observed only "a" replaces "a" and keeps the concurrent "b".
        var overA = a.Join(a.WriteDelta(R(1), "a2", a.Context));
        await Assert.That(overA.Join(b).Values.Values.Order()).IsEquivalentTo(["a2", "b"]);

        // A write over everything the replica knows leaves one value on every replica that receives it.
        var resolved = both.WriteDelta(R(2), "c");
        await Assert.That(both.Join(resolved).Values.Values).IsEquivalentTo(["c"]);
        await Assert.That(a.Join(resolved).Join(b).Values.Values).IsEquivalentTo(["c"]);
    }

    [Test]
    public async Task An_add_wins_over_a_concurrent_remove()
    {
        var a = new AwSet<string>();
        a = a.Join(a.AddDelta(R(1), "x"));
        var b = a;

        var removed = a.Join(a.RemoveDelta("x"));
        var readded = b.Join(b.AddDelta(R(2), "x"));

        await Assert.That(removed.Contains("x")).IsFalse();
        await Assert.That(removed.Join(readded).Contains("x")).IsTrue();
        await Assert.That(readded.Join(readded.RemoveDelta("x")).Join(removed).Contains("x")).IsFalse();
    }

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
    public async Task A_counter_counts_every_increment_once()
    {
        foreach (var seed in NewSeeds())
        {
            var random = new Randomizer(seed);
            var replicas = Enumerable.Repeat(new GCounter(), Replicas).ToArray();
            var deltas = new List<GCounter>();
            var total = 0UL;
            for (var step = 0; step < Steps; step++)
            {
                var replica = random.Int(0, Replicas - 1);
                var amount = random.ULong(1, 10);
                var delta = replicas[replica].IncrementDelta(R(replica + 1), amount);
                replicas[replica] = replicas[replica].Join(delta);
                deltas.Add(delta);
                total += amount;
            }

            var merged = random.Shuffle(deltas.Concat(deltas)).Aggregate(new GCounter(), (state, delta) => state.Join(delta));
            await Assert.That(merged.Value).IsEqualTo(total).Because($"seed {seed}");
        }
    }

    [Test]
    public async Task A_dot_minted_twice_with_different_values_is_refused()
    {
        // A replica that lost its state mints (r, 1) again: joining must fail loudly, not drop one write everywhere.
        var first = new MvRegister<string>().WriteDelta(R(1), "a");
        var again = new MvRegister<string>().WriteDelta(R(1), "b");

        await Assert.That(() => first.Join(again)).Throws<InvalidOperationException>();
        await Assert.That(first.Join(new MvRegister<string>().WriteDelta(R(1), "a"))).IsEqualTo(first);
    }

    [Test]
    public async Task A_new_epoch_never_mints_a_dot_its_member_minted_before()
    {
        // The member wrote "a" and shipped it, then lost it locally (an unclean start). Its next epoch writes "b".
        var shipped = new MvRegister<string>().WriteDelta(ReplicaId.Of(0, 7, 0), "a");
        var afterCrash = new MvRegister<string>().WriteDelta(ReplicaId.Of(0, 7, 1), "b");

        await Assert.That(shipped.Values.Keys.Single()).IsNotEqualTo(afterCrash.Values.Keys.Single());
        await Assert.That(shipped.Join(afterCrash).Values.Values.Order()).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task New_keys_and_failed_writes_never_reuse_a_dot()
    {
        // The database collected tombstones whose dots reached (r, 5): a key created afterwards starts above them.
        var floor = CausalContext.Of(Enumerable.Range(1, 5).Select(counter => new Dot(R(1), (ulong)counter)));
        var created = new MvRegister<string>(floor).WriteDelta(R(1), "new");
        await Assert.That(created.Values.Keys.Single()).IsEqualTo(new Dot(R(1), 6));

        // A write whose batch failed leaves its dot in the context, so the next write mints a new one.
        var state = new MvRegister<string>();
        var failed = state.WriteDelta(R(1), "lost");
        state = state.Join(new MvRegister<string>(CausalContext.Of(failed.Values.Keys)));
        await Assert.That(state.Values).IsEmpty();
        await Assert.That(state.WriteDelta(R(1), "kept").Values.Keys.Single()).IsEqualTo(new Dot(R(1), 2));
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
