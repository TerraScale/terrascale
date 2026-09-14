using System.Collections.Concurrent;
using System.Text;
using TerraBase.ZoneTree;

namespace TerraBase.ZoneTree.Tests;

public sealed class ZoneTreeTests
{
    [Test]
    public async Task UpsertAssignsMonotonicOperationIndexesAndScansInOrder()
    {
        using var tree = new ZoneTree<int, string>();

        await Assert.That(tree.Upsert(20, "twenty")).IsEqualTo(1L);
        await Assert.That(tree.Upsert(10, "ten")).IsEqualTo(2L);
        await Assert.That(tree.Upsert(20, "updated")).IsEqualTo(3L);

        var result = tree.Scan(KeyRange<int>.Between(10, 20, includeUpperBound: true));
        await Assert.That(result.Select(entry => entry.Key)).IsEquivalentTo([10, 20]);
        await Assert.That(result[1].Value).IsEqualTo("updated");
        await Assert.That(result[1].OperationIndex).IsEqualTo(3L);
    }

    [Test]
    public async Task HalfOpenRangeAndMissingDeleteDoNotExposeAdjacentKeys()
    {
        using var tree = new ZoneTree<int, string>();
        foreach (var key in Enumerable.Range(0, 5))
        {
            tree.Upsert(key, key.ToString());
        }

        var result = tree.Scan(KeyRange<int>.Between(1, 4));
        await Assert.That(result.Select(entry => entry.Key)).IsEquivalentTo([1, 2, 3]);
        await Assert.That(tree.TryRemove(99, out var missingOperation)).IsFalse();
        await Assert.That(missingOperation).IsEqualTo(0L);
    }

    [Test]
    public async Task ConcurrentWritersPreserveOneValuePerKey()
    {
        using var tree = new ZoneTree<int, int>();
        Parallel.For(0, 10_000, key => tree.Upsert(key % 64, key));

        await Assert.That(tree.Count).IsEqualTo(64);
        await Assert.That(tree.Scan().Select(entry => entry.Key).Distinct().Count()).IsEqualTo(64);
    }

    [Test]
    public async Task ScanIsSnapshotSafeAfterSubsequentMutation()
    {
        using var tree = new ZoneTree<int, string>();
        tree.Upsert(1, "before");
        var snapshot = tree.Scan();
        tree.Upsert(2, "after");

        await Assert.That(snapshot.Count).IsEqualTo(1);
        await Assert.That(snapshot[0].Value).IsEqualTo("before");
    }

    [Test]
    public async Task BrotliRoundTripsAndHonorsOutputLimit()
    {
        var source = Encoding.UTF8.GetBytes(new string('a', 10_000));
        var compressed = BrotliCompressionCodec.Instance.Compress(source);
        var restored = BrotliCompressionCodec.Instance.Decompress(compressed, source.Length);

        await Assert.That(restored).IsEquivalentTo(source);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Task.Run(() => BrotliCompressionCodec.Instance.Decompress(compressed, source.Length, source.Length - 1)));
    }

    [Test]
    public async Task NoCompressionRequiresAccurateMetadata()
    {
        var source = new byte[] { 1, 2, 3 };
        var encoded = NoCompressionCodec.Instance.Compress(source);

        await Assert.That(NoCompressionCodec.Instance.Decompress(encoded, source.Length)).IsEquivalentTo(source);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Task.Run(() => NoCompressionCodec.Instance.Decompress(encoded, source.Length - 1)));
    }
}
