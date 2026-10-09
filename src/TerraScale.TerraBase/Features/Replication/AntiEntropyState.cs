using Google.Protobuf;
using Grpc.Core;
using TerraBase.Engine;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication.Alpha;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

internal interface IAntiEntropyState
{
    ValueTask<IDatabaseStore> OpenAsync(ShardIdentity identity, CancellationToken cancellationToken);
    Task<byte[]> DigestAsync(IDatabaseStore store, CancellationToken cancellationToken);
    Task<StatePage> ReadAsync(IDatabaseStore store, RangeRequest request, CancellationToken cancellationToken);
    Task<uint> MergeAsync(IDatabaseStore store, ShardIdentity identity, ByteString envelope, CancellationToken cancellationToken);
}

internal sealed class AntiEntropyState(IStorageEngine engine, ITenantDirectory tenants, ReplicationMetrics metrics, TimeProvider time) : IAntiEntropyState
{
    internal static readonly TimeSpan DigestTimeout = TimeSpan.FromMinutes(5);
    // Covers a 2 MiB state page, framing, protobuf/encoding copies and LZ4 buffers.
    private const int WorkingBytes = 16 * 1024 * 1024;

    public async ValueTask<IDatabaseStore> OpenAsync(ShardIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Shard != 0 || identity.Revision != 0
            || !EntityIds.IsValid(identity.Organization)
            || !EntityIds.IsValid(identity.Project)
            || !EntityIds.IsValid(identity.Database)
            || identity.Incarnation.Length is not (0 or 16) || identity.Origin.Length is not (0 or 16))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid anti-entropy shard identity"));
        }

        var tenant = new TenantContext(identity.Organization, PlanTier.Hobby, identity.Project, identity.Database);
        if (await tenants.RegisteredAsync(tenant, cancellationToken).ConfigureAwait(false) is not true
            || await engine.OpenDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false) is not { } store
            || await tenants.RegisteredAsync(tenant, cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "the repair database is not registered"));
        }

        if (!identity.Incarnation.IsEmpty && store.Incarnation != new Ulid(identity.Incarnation.Span))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "the repair database was replaced"));
        }

        ObjectDisposedException.ThrowIf(store.IsClosed, store);
        return store;
    }

    public async Task<byte[]> DigestAsync(IDatabaseStore store, CancellationToken cancellationToken)
    {
        // shortcut: rebuild from bounded pages, O(keys) per pass; persist dirty tree nodes if scans become costly.
        return await MerkleTree.BuildAsync(store, (changes, token) => RepairLocalAsync(store, changes, token), time, cancellationToken).ConfigureAwait(false);
    }

    private async Task RepairLocalAsync(IDatabaseStore store, IReadOnlyList<DocumentChange> changes, CancellationToken cancellationToken)
    {
        // Restore locally retained winners before hashing; identical damage on both members
        // must not hide behind equal roots, nor win an ordinary equal-version body conflict.
        var write = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        uint repaired = 0;
        await using (write.ConfigureAwait(false))
        {
            foreach (var change in changes)
            {
                if (await write.RepairReplicationStateAsync(change, cancellationToken).ConfigureAwait(false))
                {
                    repaired++;
                }
            }

            _ = await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        metrics.Repaired(repaired);
    }

    public async Task<StatePage> ReadAsync(IDatabaseStore store, RangeRequest request, CancellationToken cancellationToken)
    {
        if (request.Ranges is 0 or > ushort.MaxValue
            || !ValidCursor(request.AfterCollection, request.AfterId)
            || request.AfterCollection.Length == 0 != (request.AfterId.Length == 0))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid repair ranges or cursor"));
        }

        ReplicationStatePage page;
        var read = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (read.ConfigureAwait(false))
        {
            await VerifyAsync(read, request.Identity, cancellationToken).ConfigureAwait(false);
            page = await read.ReadReplicationStateAsync(
                request.AfterCollection.Length == 0 ? null : request.AfterCollection,
                request.AfterId.Length == 0 ? null : request.AfterId, cancellationToken).ConfigureAwait(false);
        }

        var selected = page.Changes.Where(change => (request.Ranges & (1U << MerkleTree.Range(change))) != 0).ToArray();
        return new StatePage
        {
            HasMore = page.HasMore,
            AfterCollection = page.AfterCollection ?? string.Empty,
            AfterId = page.AfterId ?? string.Empty,
            Envelope = selected.Length == 0 ? ByteString.Empty : ByteString.CopyFrom(ApplicationEnvelope.Encode(ChangeGroup.Encode(selected)).Span),
        };
    }

    public async Task<uint> MergeAsync(IDatabaseStore store, ShardIdentity identity, ByteString envelope, CancellationToken cancellationToken)
    {
        if (envelope.Length is 0 or > ReplicationProtocol.MaxApplicationBytes)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "invalid repair envelope size"));
        }

        var changes = ChangeGroup.Decode(ApplicationEnvelope.Decode(envelope.Span, ReplicationProtocol.MaxApplicationBytes), maxChanges: 128);
        uint repaired = 0;
        var write = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (write.ConfigureAwait(false))
        {
            await VerifyAsync(write, identity, cancellationToken).ConfigureAwait(false);
            var head = await write.GetOffsetAsync(cancellationToken).ConfigureAwait(false);
            foreach (var change in changes)
            {
                VersionClock.ValidateImport(change.Version, head);
            }

            foreach (var change in changes)
            {
                if (await write.RepairReplicationStateAsync(change, cancellationToken).ConfigureAwait(false))
                {
                    repaired++;
                }
            }

            _ = await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        ObjectDisposedException.ThrowIf(store.IsClosed, store);
        metrics.Repaired(repaired);
        return repaired;
    }

    private static async Task VerifyAsync(IStorageTransaction transaction, ShardIdentity identity, CancellationToken cancellationToken)
    {
        var source = identity.SourceProof;
        var target = identity.TargetProof;
        if (identity.Origin.Length != 16 || source is null || target is null
            || !source.Origin.Equals(identity.Origin) || target.Origin.Length != 16
            || source.Sequence > long.MaxValue || target.Sequence > long.MaxValue
            || source.Digest.Length != JournalDigest.Length || target.Digest.Length != JournalDigest.Length)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "repair requires bilateral journal proofs"));
        }

        var (applied, digest) = await transaction.GetAppliedFrontierAsync(source.Origin.Memory, cancellationToken).ConfigureAwait(false);
        var origin = await transaction.GetOriginAsync(cancellationToken).ConfigureAwait(false);
        var proof = await transaction.GetChangeDigestAsync((long)target.Sequence, cancellationToken).ConfigureAwait(false);
        if ((ulong)applied > source.Sequence)
        {
            throw new RpcException(new Status(StatusCode.Aborted, "the repair journal frontier advanced"));
        }

        // A moving frontier retries next pass. Neither a reused origin nor a rolled-back file can
        // use state repair to bypass the ordinary replication journal's prefix fence.
        if ((ulong)applied != source.Sequence || !digest.Span.SequenceEqual(source.Digest.Span)
            || !origin.Span.SequenceEqual(target.Origin.Span) || proof is null
            || !proof.Value.Span.SequenceEqual(target.Digest.Span))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "repair journal history changed or requires restore recovery"));
        }
    }

    internal static IMemoryReservation Reserve(IMemoryBudget memory) => memory.TryReserve(MemoryLane.Bulk, WorkingBytes)
        ?? throw new RpcException(new Status(StatusCode.ResourceExhausted, "anti-entropy memory admission refused"));

    // Scan cursors may identify excluded keys, while transferred states must satisfy HasValidKey.
    internal static bool ValidCursor(string collection, string id) => collection.Length <= 63 && id.Length <= 1024
        && System.Text.Encoding.UTF8.GetByteCount(collection) <= 63 && System.Text.Encoding.UTF8.GetByteCount(id) <= 1024;
}
