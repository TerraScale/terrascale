using Bogus;
using TerraBase.Engine.Lattices;
using static TerraBase.Fast.Lattices.DocumentOps;

namespace TerraBase.Fast.Lattices;

// Property and fixed-byte tests of DocumentStateEncoding (docs/terrabase/multi-region-replication.md section 4). Like
// LatticeLawTests they call the lattice library directly, the exception the user approved for that library (anna-core
// decision 3). Each run draws fresh seeds; a failure names its seed.
public sealed class DocumentStateEncodingTests
{
    private const int Seeds = 40;
    private const int Steps = 30;

    // Nodes (0, 1, 0) and (0, 2, 0) and stamps 0x0102 to 0x0105, as little-endian 64-bit integers.
    private const string N1 = "0000010000000000";
    private const string N2 = "0000020000000000";
    private const string S2 = "0201000000000000";
    private const string S3 = "0301000000000000";
    private const string S4 = "0401000000000000";
    private const string S5 = "0501000000000000";

    // PUT {"a":1} by node 1 at 0x0102, inc n by -2 by node 2 at 0x0103, add "x" to s by node 1 at 0x0104 and its remove by node 2 at 0x0105.
    private const string Head = "01" + "02" + N1 + N2 + "01" + S2 + "00" + S2 + "03";
    private const string Counter = "01" + "6e" + "00" + "01" + "01" + S2 + "00" + "00" + "02" + S3 + "00";
    private const string Dot = "01" + "73" + "00" + "00" + "01" + "03227822" + S4 + "00" + "01" + S5 + "01" + S5;
    private const string Golden = Head + "01" + "61" + "02" + S2 + "00" + S2 + "0131" + "00" + "00" + Counter + Dot;
    private const string GoldenStored = Head + "01" + "61" + "03" + S2 + "00" + S2 + "00" + "00" + Counter + Dot;

    [Test]
    public async Task Every_reachable_state_and_delta_round_trips_in_both_forms()
    {
        foreach (var seed in NewSeeds())
        {
            foreach (var state in History(new Randomizer(seed)))
            {
                var full = DocumentStateEncoding.Encode(state);
                var decoded = DocumentStateEncoding.Decode(full);
                await Assert.That(decoded).IsEqualTo(state).Because($"seed {seed}");
                await Assert.That(DocumentStateEncoding.Encode(decoded).SequenceEqual(full)).IsTrue().Because($"seed {seed}");

                var stored = DocumentStateEncoding.EncodeStored(state);
                var document = state.Materialize() ?? [];
                var fromStore = DocumentStateEncoding.DecodeStored(stored, document);
                await Assert.That(fromStore).IsEqualTo(state).Because($"seed {seed}");
                await Assert.That(DocumentStateEncoding.EncodeStored(fromStore).SequenceEqual(stored)).IsTrue().Because($"seed {seed}");
                await Assert.That(DocumentStateEncoding.Digest("items", "document", fromStore)).IsEqualTo(DocumentStateEncoding.Digest("items", "document", state)).Because($"seed {seed}");
            }
        }
    }

    [Test]
    public async Task The_bytes_of_a_state_are_fixed_by_the_format()
    {
        var (n1, n2) = (ReplicaId.Of(0, 1, 0), ReplicaId.Of(0, 2, 0));
        var state = DocumentState.Bottom.Put(V(0x0102, n1), Parse("""{"a":1}"""))
            .Increment(V(0x0103, n2), "n", -2)
            .Add(V(0x0104, n1), "s", Parse("\"x\""))
            .Remove(V(0x0105, n2), "s", Parse("\"x\""));

        await Assert.That(Convert.ToHexStringLower(DocumentStateEncoding.Encode(state))).IsEqualTo(Golden);
        await Assert.That(Convert.ToHexStringLower(DocumentStateEncoding.EncodeStored(state))).IsEqualTo(GoldenStored);
        await Assert.That(state.Materialize()).IsEquivalentTo("""{"a":1,"n":-2}"""u8.ToArray());
        await Assert.That(DocumentStateEncoding.Decode(Convert.FromHexString(Golden))).IsEqualTo(state);
        await Assert.That(DocumentStateEncoding.DecodeStored(Convert.FromHexString(GoldenStored), """{"a":1,"n":-2}"""u8)).IsEqualTo(state);
    }

    [Test]
    public async Task Bookkeeping_changes_the_bytes_but_not_the_digest()
    {
        var node = ReplicaId.Of(0, 1, 0);
        var written = DocumentState.Bottom.Put(V(1, node), Parse("""{"a":1}""")).Set(V(2, node), "b", Parse("2"));
        var received = DocumentState.Bottom.Join(written.DeltaAt(new Hlc(1))).Join(written.DeltaAt(new Hlc(2)));
        var rewritten = received.Join(DocumentState.Bottom.Put(V(1, node), Parse("""{"a":1}""")));

        await Assert.That(rewritten).IsEqualTo(written);
        await Assert.That(DocumentStateEncoding.Digest("items", "document", rewritten)).IsEqualTo(DocumentStateEncoding.Digest("items", "document", written));
        await Assert.That(DocumentStateEncoding.Digest("items", "document", written.Set(V(3, node), "b", Parse("3")))).IsNotEqualTo(DocumentStateEncoding.Digest("items", "document", written));
    }

    [Test]
    public async Task Non_canonical_bytes_are_refused()
    {
        var golden = Convert.FromHexString(Golden);
        string[] refused =
        [
            "02" + Golden[2..],                                                    // unknown format
            Golden + "00",                                                          // trailing bytes
            Golden[..^2],                                                           // truncated
            "01" + "03" + N1 + N2 + "0000030000000000" + Golden[(4 + 32)..],        // a node nothing names
            "01" + "02" + N2 + N1 + Golden[(4 + 32)..],                             // nodes out of order
            Head[..^2] + "02" + Counter + "01" + "61" + "02" + S2 + "00" + S2 + "0131" + "00" + "00", // fields out of order
            Head + "01" + "61" + "02" + "0101000000000000" + "00" + S2 + "0131" + "00" + "00" + Counter + Dot, // a base below the clear
            Head + "01" + "61" + "02" + S2 + "00" + S2 + "02" + "2031" + "00" + "00" + Counter + Dot,           // a value that is not compact JSON
            Head + "01" + "61" + "02" + S2 + "8000" + S2 + "0131" + "00" + "00" + Counter + Dot,               // a node index that is not minimal
        ];

        foreach (var hex in refused)
        {
            await Assert.That(() => DocumentStateEncoding.Decode(Convert.FromHexString(hex))).Throws<InvalidDataException>().Because(hex);
        }

        // The stored form needs its own materialization as its document, and the full form never refers to one.
        await Assert.That(() => DocumentStateEncoding.Decode(Convert.FromHexString(GoldenStored))).Throws<InvalidDataException>();
        await Assert.That(() => DocumentStateEncoding.DecodeStored(Convert.FromHexString(GoldenStored), """{"a":1,"n":-3}"""u8)).Throws<InvalidDataException>();
        await Assert.That(() => DocumentStateEncoding.DecodeStored(golden, """{"a":1,"n":-2}"""u8)).Throws<InvalidDataException>();
        await Assert.That(() => DocumentStateEncoding.DecodeStored(Convert.FromHexString(GoldenStored), [])).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Corrupted_bytes_are_refused_or_decode_to_a_state_with_exactly_those_bytes()
    {
        foreach (var seed in NewSeeds())
        {
            var random = new Randomizer(seed);
            foreach (var state in History(random))
            {
                var bytes = DocumentStateEncoding.Encode(state);
                for (var flips = random.Int(1, 3); flips > 0; flips--)
                {
                    bytes[random.Int(0, bytes.Length - 1)] = random.Byte();
                }

                try
                {
                    var decoded = DocumentStateEncoding.Decode(bytes);
                    await Assert.That(DocumentStateEncoding.Encode(decoded).SequenceEqual(bytes)).IsTrue().Because($"seed {seed}");
                }
                catch (InvalidDataException)
                {
                }
            }
        }
    }

    // The states and deltas one replica reaches, with deltas received from two others.
    private static List<DocumentState> History(Randomizer random)
    {
        var replicas = new[] { DocumentState.Bottom, DocumentState.Bottom, DocumentState.Bottom };
        var seen = new List<DocumentState>();
        for (var step = 0; step < Steps; step++)
        {
            var replica = random.Int(0, replicas.Length - 1);
            var (state, stamp) = Apply(random, replicas[replica], ReplicaId.Of(0, (uint)replica + 1, 0));
            var delta = state.DeltaAt(stamp);
            replicas[replica] = state;
            replicas[0] = replicas[0].Join(delta);
            seen.Add(delta);
            seen.Add(replicas[0]);
        }

        return seen;
    }

    private static EntryVersion V(ulong stamp, ReplicaId node) => new(new Hlc(stamp), node);

    private static IEnumerable<int> NewSeeds()
    {
        var first = Random.Shared.Next(int.MaxValue - Seeds);
        return Enumerable.Range(first, Seeds);
    }
}
