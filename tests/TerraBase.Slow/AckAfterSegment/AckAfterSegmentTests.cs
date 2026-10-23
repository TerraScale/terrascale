using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Testing;

namespace TerraBase.Slow.AckAfterSegment;

/// <summary>
/// The remote-segment-before-ACK prototype (<see cref="SegmentAckGate"/>) against a fake bucket: every write it acknowledges
/// is in the history restored from the bucket alone, through added latency, lost PUT responses and an outage. This proves
/// the prototype's own contract on a fake object store, not the production shipper, not a real provider's durability,
/// and not whole-node disaster recovery.
/// </summary>
public sealed class AckAfterSegmentTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Test]
    public async Task Every_acknowledged_write_is_in_the_contiguous_history_restored_through_latency_and_lost_responses(CancellationToken cancellationToken)
    {
        // A third of the stored PUTs lose their response: the client retries the same key, which stores the same bytes again.
        var faults = new ObjectStoreFaults(seed: 7) { PutLatency = TimeSpan.FromMilliseconds(20), LostResponseChance = 0.3 };
        await using var prototype = await AckPrototype.StartAsync(faults, cancellationToken);
        await using var gate = await prototype.GateAsync(cancellationToken);

        var writes = await WriteAsync(prototype, gate, writers: 16, each: 25, Deadline, cancellationToken);

        await Assert.That(writes.Values.All(ack => ack == Ack.Durable)).IsTrue().Because("nothing in the bucket failed for longer than the deadline");
        await Assert.That(faults.LostResponses).IsGreaterThan(0);
        var (restored, position, segments) = await prototype.RestoreAsync(cancellationToken);
        await Assert.That(writes.Keys.Where(id => !restored.Contains(id)).ToList()).IsEmpty().Because("every acknowledged write is restored");
        await Assert.That(restored.Count).IsEqualTo(writes.Count).Because("nothing but the writes is restored, and none twice");
        await Assert.That(position).IsEqualTo(gate.Position);
        await Assert.That((long)segments).IsEqualTo(gate.Segments);
        await Assert.That(gate.Segments).IsLessThan(writes.Count).Because("group commit stores several writes per segment");
    }

    [Test]
    public async Task A_busy_tap_retries_without_acknowledging_partial_or_duplicate_history(CancellationToken cancellationToken)
    {
        await using var prototype = await AckPrototype.StartAsync(new ObjectStoreFaults(seed: 23), cancellationToken);
        await using var gate = await prototype.GateAsync(cancellationToken);
        var tap = (TursoChangeTap)gate.Tap;
        var trims = 0;
        var retrying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tap.BeforeTrim = async token =>
        {
            if (Interlocked.Increment(ref trims) == 1)
            {
                throw new StorageBusyException();
            }

            _ = retrying.TrySetResult();
            await release.Task.WaitAsync(token);
        };

        var writing = WriteAsync(prototype, gate, writers: 1, each: 1, Deadline, cancellationToken);
        await retrying.Task.WaitAsync(Deadline, cancellationToken);
        await Assert.That(writing.IsCompleted).IsFalse().Because("the successful retry has not finished reading the segment");
        _ = release.TrySetResult();
        var writes = await writing;

        await Assert.That(gate.Retries).IsGreaterThanOrEqualTo(1);
        await Assert.That(writes.Values.All(ack => ack == Ack.Durable)).IsTrue();
        var (restored, position, segments) = await prototype.RestoreAsync(cancellationToken);
        await Assert.That(writes.Keys.Where(id => !restored.Contains(id)).ToList()).IsEmpty();
        await Assert.That(restored.Count).IsEqualTo(writes.Count);
        await Assert.That(position).IsEqualTo(gate.Position);
        await Assert.That((long)segments).IsEqualTo(gate.Segments);
    }

    [Test]
    public async Task During_an_outage_nothing_is_acknowledged_and_every_uncertain_write_is_restored_once_the_bucket_is_back(CancellationToken cancellationToken)
    {
        var faults = new ObjectStoreFaults(seed: 11);
        await using var prototype = await AckPrototype.StartAsync(faults, cancellationToken);
        await using var gate = await prototype.GateAsync(cancellationToken);
        var before = await WriteAsync(prototype, gate, writers: 4, each: 5, Deadline, cancellationToken);

        faults.Outage = true;
        var during = await WriteAsync(prototype, gate, writers: 4, each: 5, TimeSpan.FromSeconds(1), cancellationToken);
        await Assert.That(during.Values.All(ack => ack == Ack.Uncertain)).IsTrue().Because("no segment can be stored during the outage");
        await Assert.That(faults.Refused).IsGreaterThan(0);

        faults.Outage = false;
        var after = await WriteAsync(prototype, gate, writers: 1, each: 1, Deadline, cancellationToken);
        await Assert.That(after.Values.Single()).IsEqualTo(Ack.Durable);

        // An uncertain answer is not a loss: those writes committed locally and were stored once the bucket came back.
        var (restored, _, _) = await prototype.RestoreAsync(cancellationToken);
        await Assert.That(before.Keys.Concat(during.Keys).Concat(after.Keys).Where(id => !restored.Contains(id)).ToList()).IsEmpty();
    }

    [Test]
    public async Task A_generation_acknowledges_nothing_until_its_snapshot_is_stored(CancellationToken cancellationToken)
    {
        var faults = new ObjectStoreFaults(seed: 13) { Outage = true };
        await using var prototype = await AckPrototype.StartAsync(faults, cancellationToken);
        _ = await prototype.WriteAsync(cancellationToken);

        var time = new FakeTimeProvider();
        var starting = prototype.GateAsync(cancellationToken, time);
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(faults.Refused > 0));
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(starting.IsCompleted).IsFalse().Because("the snapshot cannot be stored during the outage");

        faults.Outage = false;
        await using var gate = await starting.WaitAsync(Deadline, cancellationToken);
        await Assert.That(gate.SnapshotStall).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        var (restored, position, _) = await prototype.RestoreAsync(cancellationToken);
        await Assert.That(restored.Count).IsEqualTo(1).Because("the write before the generation started is in its snapshot");
        await Assert.That(position).IsEqualTo(0);
    }

    [Test]
    public async Task Without_the_gate_a_write_acknowledged_by_its_local_commit_is_missing_from_the_restored_history(CancellationToken cancellationToken)
    {
        // The control: today's acknowledgement is the 2xx after the local commit, before anything reaches the bucket.
        var faults = new ObjectStoreFaults(seed: 17);
        await using var prototype = await AckPrototype.StartAsync(faults, cancellationToken);
        await using var gate = await prototype.GateAsync(cancellationToken);
        var shipped = await WriteAsync(prototype, gate, writers: 1, each: 3, Deadline, cancellationToken);

        var unshipped = new List<string>();
        for (var write = 0; write < 3; write++)
        {
            unshipped.Add(await prototype.WriteAsync(cancellationToken));
        }

        var (restored, _, _) = await prototype.RestoreAsync(cancellationToken);
        await Assert.That(shipped.Keys.All(restored.Contains)).IsTrue();
        await Assert.That(unshipped.Where(restored.Contains).ToList()).IsEmpty().Because("acknowledged locally, never stored in the bucket");
    }

    [Test]
    public async Task A_missing_segment_fails_the_restore_as_a_gap_instead_of_restoring_a_shorter_history(CancellationToken cancellationToken)
    {
        var faults = new ObjectStoreFaults(seed: 19);
        await using var prototype = await AckPrototype.StartAsync(faults, cancellationToken);
        await using var gate = await prototype.GateAsync(cancellationToken);
        for (var write = 0; write < 3; write++)
        {
            // One writer, one write per segment.
            _ = await WriteAsync(prototype, gate, writers: 1, each: 1, Deadline, cancellationToken);
        }

        await Assert.That(gate.Segments).IsEqualTo(3);
        List<string> segments = [];
        await foreach (var stored in prototype.Store.ListAsync("ack-proto/generation-1/segment-", null, cancellationToken))
        {
            segments.Add(stored.Key);
        }

        _ = await prototype.Store.DeleteAsync(segments[1], cancellationToken);
        var gap = await Assert.ThrowsAsync<InvalidDataException>(() => prototype.RestoreAsync(cancellationToken));
        await Assert.That(gap!.Message).Contains("a gap or an overlap");
    }

    // Writers each write `each` documents one after another; after each local commit they wait for its acknowledgement.
    internal static async Task<ConcurrentDictionary<string, Ack>> WriteAsync(
        AckPrototype prototype, SegmentAckGate gate, int writers, int each, TimeSpan deadline, CancellationToken cancellationToken)
    {
        var acks = new ConcurrentDictionary<string, Ack>(StringComparer.Ordinal);
        await Task.WhenAll(Enumerable.Range(0, writers).Select(_ => Task.Run(async () =>
        {
            for (var write = 0; write < each; write++)
            {
                var id = await prototype.WriteAsync(cancellationToken);
                acks[id] = await gate.AckAsync(deadline, cancellationToken);
            }
        }, cancellationToken)));
        return acks;
    }
}
