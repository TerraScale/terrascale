using System.Text;
using Bogus;
using TerraBase.Engine.Lattices;
using static TerraBase.Fast.Lattices.DocumentOps;

namespace TerraBase.Fast.Lattices;

// The document lattice of docs/terrabase/multi-region-replication.md section 4: convergence of whole replicas, the
// resolution rules and the encodings. Like LatticeLawTests they call the lattice library directly, the exception the
// user approved for that library (anna-core decision 3). Each run draws fresh seeds; a failure names its seed.
public sealed class DocumentStateTests
{
    private const int Seeds = 60;
    private const int Replicas = 3;
    private const int Steps = 40;

    private static readonly ReplicaId A = ReplicaId.Of(1, 1, 0);
    private static readonly ReplicaId B = ReplicaId.Of(2, 1, 0);

    [Test]
    public async Task Replicas_converge_to_one_document_and_digest_under_any_delivery()
    {
        foreach (var seed in NewSeeds())
        {
            var random = new Randomizer(seed);
            var replicas = Enumerable.Repeat(DocumentState.Bottom, Replicas).ToArray();
            var deltas = new List<DocumentState>();
            for (var step = 0; step < Steps; step++)
            {
                var replica = random.Int(0, Replicas - 1);
                if (deltas.Count == 0 || random.Bool())
                {
                    var before = replicas[replica];
                    var (after, stamp) = Apply(random, before, R(replica));
                    var delta = after.DeltaAt(stamp);

                    // The delta carries the whole operation: joining it gives what the operation gave.
                    await Assert.That(before.Join(delta)).IsEqualTo(after).Because($"delta of an operation, seed {seed}");
                    replicas[replica] = after;
                    deltas.Add(delta);
                }
                else
                {
                    replicas[replica] = replicas[replica].Join(random.ListItem(deltas));
                }
            }

            var converged = replicas
                .Select(state => random.Shuffle(deltas.Concat(deltas)).Aggregate(state, (current, delta) => current.Join(delta)))
                .ToArray();
            var document = converged[0].Materialize();
            foreach (var state in converged)
            {
                await Assert.That(state).IsEqualTo(converged[0]).Because($"state, seed {seed}");
                await Assert.That(Text(state.Materialize())).IsEqualTo(Text(document)).Because($"document, seed {seed}");
                await Assert.That(DocumentStateEncoding.Digest(state)).IsEqualTo(DocumentStateEncoding.Digest(converged[0])).Because($"digest, seed {seed}");
            }
        }
    }

    [Test]
    public async Task Concurrent_writes_to_one_field_keep_the_newest_version_on_every_replica()
    {
        var (a, b) = Created();
        var fromA = a.Set(V(3, A), "name", Parse("\"a\""));
        var fromB = b.Set(V(3, B), "name", Parse("\"b\""));
        var older = b.Set(V(2, B), "other", Parse("1")).Set(V(2, B), "name", Parse("\"stale\""));

        await Assert.That(Text(fromA.Join(fromB).Materialize())).IsEqualTo("""{"name":"b","seed":0}""");
        await Assert.That(Text(fromB.Join(fromA).Materialize())).IsEqualTo("""{"name":"b","seed":0}""");
        await Assert.That(Text(fromA.Join(older).Materialize())).IsEqualTo("""{"name":"a","other":1,"seed":0}""");
    }

    [Test]
    public async Task Concurrent_writes_to_different_fields_both_survive()
    {
        var (a, b) = Created();
        var merged = a.Set(V(3, A), "x", Parse("1")).Join(b.Set(V(3, B), "y", Parse("2")).Unset(V(3, B), "seed"));

        await Assert.That(Text(merged.Materialize())).IsEqualTo("""{"x":1,"y":2}""");
    }

    [Test]
    public async Task A_delete_wins_over_older_edits_and_a_newer_edit_brings_back_only_itself()
    {
        var (a, b) = Created();
        var deleted = a.Delete(V(5, A));

        // An edit older than the delete is gone, whatever the order.
        var stale = b.Set(V(4, B), "late", Parse("1"));
        await Assert.That(deleted.Join(stale).Materialize()).IsNull();
        await Assert.That(stale.Join(deleted).Exists).IsFalse();

        // An edit newer than the delete brings the document back with only the fields written after the delete.
        var revived = b.Set(V(6, B), "late", Parse("1")).Join(deleted);
        await Assert.That(Text(revived.Materialize())).IsEqualTo("""{"late":1}""");

        // A newer unset does not bring it back.
        await Assert.That(b.Unset(V(6, B), "seed").Join(deleted).Exists).IsFalse();
    }

    [Test]
    public async Task Counters_sum_concurrent_increments_and_a_set_starts_a_new_epoch()
    {
        var (a, b) = Created();
        a = a.Increment(V(3, A), "n", 5).Increment(V(4, A), "n", -1);
        b = b.Increment(V(3, B), "n", 10);
        var merged = a.Join(b);
        await Assert.That(Text(merged.Materialize())).IsEqualTo("""{"n":14,"seed":0}""");

        // A set resets the counter; an increment concurrent with it lands in the old epoch and is lost.
        var reset = merged.Set(V(5, A), "n", Parse("0"));
        var concurrent = merged.Increment(V(5, B), "n", 7);
        await Assert.That(Text(reset.Join(concurrent).Materialize())).IsEqualTo("""{"n":0,"seed":0}""");

        // Increments after the reset count from the new base.
        var after = reset.Join(concurrent).Increment(V(6, B), "n", 2).Join(reset.Increment(V(6, A), "n", 3));
        await Assert.That(Text(after.Materialize())).IsEqualTo("""{"n":5,"seed":0}""");

        // An integer base counts too.
        await Assert.That(Text(reset.Set(V(7, A), "n", Parse("40")).Increment(V(8, A), "n", 2).Materialize())).IsEqualTo("""{"n":42,"seed":0}""");
    }

    [Test]
    public async Task An_add_wins_over_a_concurrent_remove_and_a_remove_takes_what_it_saw()
    {
        var (a, b) = Created();
        a = a.Add(V(3, A), "tags", Parse("\"x\""));
        b = b.Join(a);

        var removed = a.Remove(V(4, A), "tags", Parse("\"x\""));
        var readded = b.Add(V(4, B), "tags", Parse("\"x\""));
        await Assert.That(Text(removed.Materialize())).IsEqualTo("""{"seed":0}""");
        await Assert.That(Text(removed.Join(readded).Materialize())).IsEqualTo("""{"seed":0,"tags":["x"]}""");

        var both = removed.Join(readded).Remove(V(5, A), "tags", Parse("\"x\""));
        await Assert.That(Text(both.Join(readded).Join(removed).Materialize())).IsEqualTo("""{"seed":0}""");
    }

    [Test]
    public async Task An_array_base_becomes_a_set_on_its_first_set_operation()
    {
        var (a, _) = Created();
        a = a.Set(V(3, A), "s", Parse("[3,1,1]"));
        await Assert.That(Text(a.Materialize())).IsEqualTo("""{"s":[3,1,1],"seed":0}""");

        a = a.Add(V(4, A), "s", Parse("2"));
        await Assert.That(Text(a.Materialize())).IsEqualTo("""{"s":[1,2,3],"seed":0}""");

        a = a.Remove(V(5, A), "s", Parse("1")).Remove(V(6, A), "s", Parse("2")).Remove(V(7, A), "s", Parse("3"));
        await Assert.That(Text(a.Materialize())).IsEqualTo("""{"s":[],"seed":0}""");
    }

    [Test]
    public async Task Kinds_resolve_by_precedence_and_a_newer_base_beats_them_all()
    {
        var (a, b) = Created();
        var counted = a.Increment(V(3, A), "f", 1);
        var added = b.Add(V(3, B), "f", Parse("\"x\""));

        // Concurrent increment and add on an absent field make a set.
        await Assert.That(Text(counted.Join(added).Materialize())).IsEqualTo("""{"f":["x"],"seed":0}""");

        // A newer plain write replaces both.
        var plain = a.Set(V(4, A), "f", Parse("\"p\""));
        await Assert.That(Text(counted.Join(added).Join(plain).Materialize())).IsEqualTo("""{"f":"p","seed":0}""");
    }

    [Test]
    public async Task Fields_sort_by_their_utf8_bytes_and_elements_by_their_compact_json()
    {
        // UTF-16 order puts the surrogate pair of 😀 before U+FFFD; UTF-8 (code point) order puts it after. Compact JSON
        // escapes characters outside the BMP, so as a set element 😀 sorts as its escape, before U+FFFD.
        var state = DocumentState.Bottom.Put(V(1, A), Parse("""{"😀":1,"\uFFFD":2,"b":3}"""))
            .Add(V(2, A), "a", Parse("\"😀\"")).Add(V(2, A), "a", Parse("\"\uFFFD\""));

        await Assert.That(Text(state.Materialize())).IsEqualTo("{\"a\":[\"\\uD83D\\uDE00\",\"\uFFFD\"],\"b\":3,\"\uFFFD\":2,\"\\uD83D\\uDE00\":1}");
    }

    [Test]
    public async Task Operations_on_the_wrong_kind_or_with_a_stale_stamp_are_refused()
    {
        var (a, _) = Created();
        var text = a.Set(V(3, A), "t", Parse("\"x\""));

        await Assert.That(() => text.Increment(V(4, A), "t", 1)).Throws<InvalidOperationException>();
        await Assert.That(() => text.Add(V(4, A), "t", Parse("1"))).Throws<InvalidOperationException>();
        await Assert.That(() => text.Increment(V(4, A), "seed", 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => text.Set(V(2, A), "t", Parse("1"))).Throws<ArgumentOutOfRangeException>();
    }

    private static (DocumentState A, DocumentState B) Created()
    {
        var created = DocumentState.Bottom.Put(V(1, A), Parse("""{"seed":0}"""));
        return (created, created);
    }

    private static EntryVersion V(ulong stamp, ReplicaId node) => new(new Hlc(stamp), node);

    private static string? Text(byte[]? json) => json is null ? null : Encoding.UTF8.GetString(json);

    private static IEnumerable<int> NewSeeds()
    {
        var first = Random.Shared.Next(int.MaxValue - Seeds);
        return Enumerable.Range(first, Seeds);
    }

    private static ReplicaId R(int member) => ReplicaId.Of(0, (uint)member + 1, 0);
}
