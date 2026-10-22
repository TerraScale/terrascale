using System.Buffers.Binary;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replication.V1;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replication;

/// <summary>Complete, key-validated coverage from one pinned intact source, followed by the engine's FULL barrier.</summary>
internal sealed class RebuildCertification(
    IMembership membership, ITenantDirectory tenants, IRebuildRepair repair, IMemoryBudget memory,
    ILogger<RebuildCertification> logger, PeerCredential credential) : IRebuildCertification
{
    private const int DigestBytes = 4096 * 16;
    private const long ReservedBytes = 3L * DigestBytes + 4L * ReplicationProtocol.MaxApplicationBytes;
    private static readonly TimeSpan DigestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefillTimeout = TimeSpan.FromMinutes(10);
    private const int MaxRounds = 8;

    public async ValueTask<bool> RefillAsync(TenantContext database, Ulid newIncarnation, CancellationToken cancellationToken)
    {
        if (database == ChangeGroups.System || await tenants.DatabaseRecordAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord record)
        {
            return false;
        }

        // Host recovery has one bounded pass, like the ordinary refill receiver, outside foreground tenant admission.
        using var reservation = memory.TryReserve(MemoryLane.Bulk, ReservedBytes, tenant: null);
        if (reservation is null)
        {
            logger.ZLogWarning($"The receiver has no memory capacity to start a certified rebuild refill");
            return false;
        }

        var view = await membership.RefreshAsync(cancellationToken).ConfigureAwait(false);
        foreach (var peer in view.Members.Concat(view.Elsewhere.IsDefault ? [] : view.Elsewhere)
            .Where(peer => peer.Name != membership.Self.Name && peer.PeerAddress.Length > 0 && record.Placement.Includes(peer.Region)))
        {
            try
            {
                if (await RefillFromAsync(database, newIncarnation, peer, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (RpcException error) when (cancellationToken.IsCancellationRequested && error.StatusCode == StatusCode.Cancelled)
            {
                throw new OperationCanceledException("the rebuild refill was cancelled", error, cancellationToken);
            }
            catch (InvalidDataException error) when (StoredDataCorruption.Is(error))
            {
                logger.ZLogWarning(error, $"The receiver of a certified rebuild refill has corrupt stored data");
                return false;
            }
            catch (ReplicaCopyFaultedException error)
            {
                logger.ZLogWarning(error, $"The receiver of a certified rebuild refill is faulted");
                return false;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested
                && error is QosMemoryException or StorageBusyException)
            {
                logger.ZLogWarning(error, $"The receiver has no memory capacity for a certified rebuild refill");
                return false;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested
                && error is RpcException or InvalidDataException or IOException)
            {
                logger.ZLogWarning(error, $"A certified rebuild refill from {peer.Name:@Peer} was refused");
            }
        }

        return false;
    }

    public ValueTask<bool> CompleteAsync(TenantContext database, Ulid oldIncarnation, Ulid newIncarnation, CancellationToken cancellationToken) =>
        repair.CompleteRebuildAsync(database, oldIncarnation, newIncarnation, cancellationToken);

    private async Task<bool> RefillFromAsync(TenantContext database, Ulid receiver, ClusterMember peer, CancellationToken cancellationToken)
    {
        using var channel = PeerChannel.Create(new Uri(peer.PeerAddress), credential);
        var client = new V1.Replication.ReplicationClient(channel);
        var initial = await client.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(database, null) },
            deadline: TimeProvider.System.GetUtcNow().UtcDateTime + DigestTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!initial.Held || !initial.HasCollectedBefore || initial.CollectedBefore > long.MaxValue)
        {
            return false;
        }

        var source = Incarnation(initial);
        _ = Digests(initial);
        var observed = new byte[DigestBytes];
        var requested = Enumerable.Repeat(true, 4096).ToArray();
        var collectedBefore = new Hlc(initial.CollectedBefore);
        var head = initial.HeadChangeId;
        for (var round = 0; round < MaxRounds; round++)
        {
            var buckets = Enumerable.Range(0, 4096).Where(bucket => requested[bucket]).ToArray();
            var request = new BucketRequest { Database = ChangeGroups.Wire(database, source) };
            foreach (var bucket in buckets)
            {
                request.Buckets.Add((uint)bucket);
                observed.AsSpan(bucket * 16, 16).Clear();
            }

            BucketCursor? previous = null;
            using (var call = client.ReadBucket(request, deadline: TimeProvider.System.GetUtcNow().UtcDateTime + RefillTimeout, cancellationToken: cancellationToken))
            {
                while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    if (call.ResponseStream.Current.Incarnation.Length != 16 || new Ulid(call.ResponseStream.Current.Incarnation.Span) != source)
                    {
                        return false;
                    }

                    var bytes = ApplicationEnvelope.Decode(call.ResponseStream.Current.States.Span, ReplicationProtocol.MaxApplicationBytes);
                    // Decoded lattice nodes and JSON parsing need admission beyond the transport and protobuf buffers.
                    using var decoded = memory.TryReserve(MemoryLane.Bulk, 16L * bytes.Length, tenant: null)
                        ?? throw new QosMemoryException("the node has no memory capacity to decode a rebuild page");

                    var page = Decode(database, bytes);
                    var after = previous;
                    foreach (var state in page)
                    {
                        var bucket = DocumentStateEncoding.Bucket(state.Collection, state.Id);
                        if (!requested[bucket] || previous is not null && (bucket < previous.Bucket || bucket == previous.Bucket
                            && (CompareKey(state.Collection, previous.Collection) < 0 || state.Collection == previous.Collection
                                && CompareKey(state.Id, previous.Id) <= 0)))
                        {
                            throw new InvalidDataException("a rebuild source returned an unrequested, repeated or reordered state key");
                        }

                        var digest = DocumentStateEncoding.Digest(state.Collection, state.Id, state.State);
                        var offset = bucket * 16;
                        BinaryPrimitives.WriteUInt64LittleEndian(observed.AsSpan(offset),
                            BinaryPrimitives.ReadUInt64LittleEndian(observed.AsSpan(offset)) ^ (ulong)(digest >> 64));
                        BinaryPrimitives.WriteUInt64LittleEndian(observed.AsSpan(offset + 8),
                            BinaryPrimitives.ReadUInt64LittleEndian(observed.AsSpan(offset + 8)) ^ (ulong)digest);
                        previous = new BucketCursor(bucket, state.Collection, state.Id);
                    }

                    if (await repair.RefillRebuildAsync(database, receiver, buckets, after, page, collectedBefore, cancellationToken).ConfigureAwait(false) is null)
                    {
                        return false;
                    }
                }
            }

            if (await repair.RefillRebuildAsync(database, receiver, buckets, previous, [], collectedBefore, cancellationToken).ConfigureAwait(false) is null)
            {
                return false;
            }

            var final = await client.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(database, source) },
                deadline: TimeProvider.System.GetUtcNow().UtcDateTime + DigestTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!final.Held || !final.HasCollectedBefore || final.CollectedBefore > long.MaxValue || final.CollectedBefore < collectedBefore.Value
                || Incarnation(final) != source || final.HeadChangeId < head)
            {
                return false;
            }

            var expected = Digests(final);
            if (final.CollectedBefore == collectedBefore.Value && observed.AsSpan().SequenceEqual(expected))
            {
                return await repair.RecordRefillAsync(database, receiver, peer.Name, source, checked((long)final.HeadChangeId), new Hlc(final.CollectedBefore), cancellationToken)
                    .ConfigureAwait(false);
            }

            // A newer absence frontier requires coverage of all receiver keys, including already matching buckets.
            for (var bucket = 0; bucket < requested.Length; bucket++)
            {
                requested[bucket] = final.CollectedBefore != collectedBefore.Value
                    || !observed.AsSpan(bucket * 16, 16).SequenceEqual(expected.AsSpan(bucket * 16, 16));
            }

            collectedBefore = new Hlc(final.CollectedBefore);
            head = final.HeadChangeId;
        }

        return false;
    }

    private static Ulid Incarnation(DigestReply reply) =>
        reply.Incarnation.Length == 16 && new Ulid(reply.Incarnation.Span) is var incarnation && incarnation != Ulid.Empty
            && reply.HeadChangeId <= long.MaxValue ? incarnation : throw new InvalidDataException("a rebuild source returned an invalid incarnation or head");

    private static byte[] Digests(DigestReply reply)
    {
        var digests = ApplicationEnvelope.Decode(reply.Buckets.Span, DigestBytes);
        return digests.Length == DigestBytes ? digests : throw new InvalidDataException("a rebuild source did not return all 4096 bucket digests");
    }

    private static List<RepairState> Decode(TenantContext database, byte[] bytes)
    {
        StateList list;
        try
        {
            // Count wire entries before the parser materializes them, so many tiny entries cannot bypass page admission.
            using var input = new CodedInputStream(bytes);
            var count = 0;
            while (input.ReadTag() is var tag && tag != 0)
            {
                if (tag != 10 || ++count > ChangeGroups.MaxDeltas)
                {
                    throw new InvalidDataException("a rebuild source page exceeds the state count or has an unknown field");
                }

                input.SkipLastField();
            }

            list = StateList.Parser.ParseFrom(bytes);
        }
        catch (InvalidProtocolBufferException error)
        {
            throw new InvalidDataException("a rebuild source page is not a state list", error);
        }

        if (list.States.Count is 0 or > ChangeGroups.MaxDeltas)
        {
            throw new InvalidDataException("a rebuild source page has 1 to 1024 states");
        }

        var states = new List<RepairState>(list.States.Count);
        foreach (var entry in list.States)
        {
            if (!ChangeGroups.ValidKey(database, entry.Collection, entry.Id))
            {
                throw new InvalidDataException("a rebuild source page has an invalid state key");
            }

            var state = DocumentStateEncoding.Decode(entry.State.Span);
            if (state.MaxStamp.Value == 0)
            {
                throw new InvalidDataException("a rebuild source page has an unstamped state");
            }

            states.Add(new RepairState(entry.Collection, entry.Id, state));
        }

        return states;
    }

    // SQLite's BINARY collation orders UTF-8 bytes, including supplementary Unicode characters.
    private static int CompareKey(string left, string right) => Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right));
}
