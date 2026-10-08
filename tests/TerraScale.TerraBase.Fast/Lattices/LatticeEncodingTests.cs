using System.Text;
using Bogus;
using TerraBase.Engine.Lattices;

namespace TerraBase.Fast.Lattices;

// Property and fixed-byte tests of the document register encoding (TS-647 step 4a, docs/design/anna-core.md Encoding).
// Like LatticeLawTests they call the lattice library directly, the exception the user approved for that library
// (2026-10-07). Each run draws fresh seeds; a failure names its seed.
public sealed class LatticeEncodingTests
{
    private const int Seeds = 40;
    private const int Replicas = 3;
    private const int Steps = 30;

    // Replica (0, 1, 0) and (0, 2, 0), counters 1 to 3 and the stamp 0x0102, as little-endian 64-bit integers.
    private const string R1 = "0000010000000000";
    private const string R2 = "0000020000000000";
    private const string C1 = "0100000000000000";
    private const string C2 = "0200000000000000";
    private const string C3 = "0300000000000000";
    private const string Stamp = "0201000000000000";

    // Prefix (R1, 1), cloud dot (R2, 2), the write {} by (R1, 1) at 0x0102 and a tombstone by (R2, 2) at 3.
    private const string Golden = "01" + "01" + R1 + C1 + "01" + R2 + C2 + "02" + R1 + C1 + Stamp + "01" + "02" + "7b7d" + R2 + C2 + C3 + "00";

    [Test]
    public async Task Every_reachable_register_decodes_to_the_same_state_and_bytes()
    {
        foreach (var seed in NewSeeds())
        {
            foreach (var state in History(new Randomizer(seed)))
            {
                var bytes = LatticeEncoding.Encode(state);
                var decoded = LatticeEncoding.DecodeDocument(bytes);
                await Assert.That(decoded).IsEqualTo(state).Because($"seed {seed}");
                await Assert.That(LatticeEncoding.Encode(decoded).SequenceEqual(bytes)).IsTrue().Because($"seed {seed}");
            }
        }
    }

    [Test]
    public async Task Equal_states_reached_in_any_order_encode_to_equal_bytes()
    {
        foreach (var seed in NewSeeds())
        {
            var random = new Randomizer(seed);
            var states = History(random);
            for (var check = 0; check < Steps; check++)
            {
                var (a, b, c) = (random.ListItem(states), random.ListItem(states), random.ListItem(states));
                var one = LatticeEncoding.Encode(a.Join(b).Join(c));
                var other = LatticeEncoding.Encode(c.Join(a.Join(b)));
                await Assert.That(one.SequenceEqual(other)).IsTrue().Because($"seed {seed}");
            }
        }
    }

    [Test]
    public async Task The_bytes_of_a_register_are_fixed_by_the_format()
    {
        var written = new MvRegister<DocumentWrite>().WriteDelta(ReplicaId.Of(0, 1, 0), DocumentWrite.Live(new Hlc(0x0102), "{}"u8));
        await Assert.That(Convert.ToHexStringLower(LatticeEncoding.Encode(written)))
            .IsEqualTo("01" + "01" + R1 + C1 + "00" + "01" + R1 + C1 + Stamp + "01" + "02" + "7b7d");

        var decoded = LatticeEncoding.DecodeDocument(Convert.FromHexString(Golden));
        var (first, second) = (new Dot(ReplicaId.Of(0, 1, 0), 1), new Dot(ReplicaId.Of(0, 2, 0), 2));
        await Assert.That(decoded.Values.Count).IsEqualTo(2);
        await Assert.That(decoded.Values[first].Stamp).IsEqualTo(new Hlc(0x0102));
        await Assert.That(decoded.Values[first].Body.SequenceEqual("{}"u8)).IsTrue();
        await Assert.That(decoded.Values[second]).IsEqualTo(DocumentWrite.Tombstone(new Hlc(3)));
        await Assert.That(decoded.Context.Contains(second) && !decoded.Context.Contains(second with { Counter = 1 })).IsTrue();
        await Assert.That(Convert.ToHexStringLower(LatticeEncoding.Encode(decoded))).IsEqualTo(Golden);
    }

    [Test]
    public async Task Every_truncation_of_a_register_is_refused()
    {
        var bytes = Convert.FromHexString(Golden);
        for (var length = 0; length < bytes.Length; length++)
        {
            await Assert.That(() => LatticeEncoding.DecodeDocument(bytes.AsSpan(0, length))).Throws<InvalidDataException>().Because($"{length} bytes");
        }
    }

    [Test]
    [Arguments("an unknown format", "02" + "00" + "00" + "00")]
    [Arguments("trailing bytes", Golden + "00")]
    [Arguments("replicas out of order", "01" + "02" + R2 + C1 + R1 + C1 + "00" + "00")]
    [Arguments("a replica twice", "01" + "02" + R1 + C1 + R1 + C2 + "00" + "00")]
    [Arguments("a zero prefix", "01" + "01" + R1 + "0000000000000000" + "00" + "00")]
    [Arguments("a cloud dot inside its prefix", "01" + "01" + R1 + C2 + "01" + R1 + C1 + "00" + "00")]
    [Arguments("a cloud dot extending its prefix", "01" + "01" + R1 + C1 + "01" + R1 + C2 + "00" + "00")]
    [Arguments("cloud dots out of order", "01" + "00" + "02" + R2 + C3 + R1 + C3 + "00")]
    [Arguments("a write outside its context", "01" + "00" + "00" + "01" + R1 + C1 + Stamp + "00")]
    [Arguments("a write at counter zero", "01" + "00" + "00" + "01" + R1 + "0000000000000000" + Stamp + "00")]
    [Arguments("a write of an unknown kind", "01" + "01" + R1 + C1 + "00" + "01" + R1 + C1 + Stamp + "02")]
    [Arguments("writes out of order", "01" + "01" + R1 + C2 + "00" + "02" + R1 + C2 + Stamp + "00" + R1 + C1 + Stamp + "00")]
    [Arguments("a length that is not minimal", "01" + "01" + R1 + C1 + "00" + "01" + R1 + C1 + Stamp + "01" + "8000")]
    [Arguments("a length beyond the bytes", "01" + "01" + R1 + C1 + "00" + "01" + R1 + C1 + Stamp + "01" + "05" + "7b7d")]
    [Arguments("a length beyond its limit", "01" + "01" + R1 + C1 + "00" + "01" + R1 + C1 + Stamp + "01" + "ffffffff0f")]
    [Arguments("a count beyond the bytes", "01" + "05" + R1 + C1 + "00" + "00")]
    public async Task Malformed_or_non_canonical_registers_are_refused(string problem, string hex)
    {
        var bytes = Convert.FromHexString(hex);
        await Assert.That(() => LatticeEncoding.DecodeDocument(bytes)).Throws<InvalidDataException>().Because(problem);
    }

    [Test]
    public async Task A_write_keeps_its_own_copy_of_the_body_it_was_given()
    {
        var body = "{\"v\":1}"u8.ToArray();
        var write = DocumentWrite.Live(new Hlc(1), body);
        var register = new MvRegister<DocumentWrite>().WriteDelta(ReplicaId.Of(0, 1, 0), write);
        var bytes = LatticeEncoding.Encode(register);
        body[5] = (byte)'2';
        await Assert.That(write.Body.SequenceEqual("{\"v\":1}"u8)).IsTrue();
        await Assert.That(LatticeEncoding.Encode(register).SequenceEqual(bytes)).IsTrue();
        await Assert.That(register.Join(LatticeEncoding.DecodeDocument(bytes))).IsEqualTo(register);
    }

    [Test]
    public async Task Dots_and_replicas_whose_halves_cancel_still_hash_apart()
    {
        // ulong's own hash is its halves XORed, which is zero for every value below.
        var values = Enumerable.Range(1, 1000).Select(k => (ulong)k * 0x1_0000_0001).ToArray();
        await Assert.That(values.Select(value => new Dot(ReplicaId.Of(0, 1, 0), value).GetHashCode()).Distinct().Count()).IsGreaterThan(990);
        await Assert.That(values.Select(value => new ReplicaId(value).GetHashCode()).Distinct().Count()).IsGreaterThan(990);
    }

    // Replicas write live documents and tombstones, over everything they know or over an earlier state only, and join
    // each other's states and deltas, so the history holds concurrent writes, clouds and joins of every kind.
    private static List<MvRegister<DocumentWrite>> History(Randomizer random)
    {
        var ids = Enumerable.Range(0, Replicas).Select(_ => ReplicaId.Of((ushort)random.Int(0, ushort.MaxValue), random.UInt(), (ushort)random.Int(0, ushort.MaxValue))).ToArray();
        var replicas = Enumerable.Repeat(new MvRegister<DocumentWrite>(), Replicas).ToArray();
        var seen = new List<MvRegister<DocumentWrite>> { new() };
        var clock = new Hlc(0);
        for (var step = 0; step < Steps; step++)
        {
            var index = random.Int(0, Replicas - 1);
            if (random.Bool(0.6f))
            {
                clock = clock.Tick(random.Long(0, 1L << 40));
                var write = random.Bool(0.2f)
                    ? DocumentWrite.Tombstone(clock)
                    : DocumentWrite.Live(clock, Encoding.UTF8.GetBytes($$"""{"v":"{{random.AlphaNumeric(random.Int(0, 300))}}"}"""));
                var delta = random.Bool() ? replicas[index].WriteDelta(ids[index], write) : replicas[index].WriteDelta(ids[index], write, random.ListItem(seen).Context);
                seen.Add(delta);
                replicas[index] = replicas[index].Join(delta);
            }
            else
            {
                replicas[index] = replicas[index].Join(random.ListItem(seen));
            }

            seen.Add(replicas[index]);
        }

        return seen;
    }

    private static IEnumerable<int> NewSeeds()
    {
        var first = Random.Shared.Next(int.MaxValue - Seeds);
        return Enumerable.Range(first, Seeds);
    }
}
