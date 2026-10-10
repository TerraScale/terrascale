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
    Task<uint> MergeAsync(IDatabaseStore store, ByteString envelope, CancellationToken cancellationToken);
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
            || identity.Incarnation.Length is not (0 or 16))
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

    public async Task<byte[]> DigestAsync(IDatabaseStore store, CancellationToken cancellationToken) =>
        // shortcut: rebuild from bounded pages, O(keys) per pass; persist dirty tree nodes if scans become costly.
        await MerkleTree.BuildAsync(store, time, cancellationToken).ConfigureAwait(false);

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
            page = await read.ReadReplicationStateAsync(
                request.AfterCollection.Length == 0 ? null : request.AfterCollection,
                request.AfterId.Length == 0 ? null : request.AfterId, cancellationToken).ConfigureAwait(false);
        }

        var selected = page.States.Where(state => (request.Ranges & (1U << MerkleTree.Range(state.Collection, state.Id))) != 0).ToArray();
        return new StatePage
        {
            HasMore = page.HasMore,
            AfterCollection = page.AfterCollection ?? string.Empty,
            AfterId = page.AfterId ?? string.Empty,
            Envelope = selected.Length == 0 ? ByteString.Empty
                : ByteString.CopyFrom(ApplicationEnvelope.Encode(DeltaGroup.Encode(new CapturedGroup([], selected), out _)).Span),
        };
    }

    public async Task<uint> MergeAsync(IDatabaseStore store, ByteString envelope, CancellationToken cancellationToken)
    {
        if (envelope.Length is 0 or > ReplicationProtocol.MaxApplicationBytes)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "invalid repair envelope size"));
        }

        var group = DeltaGroup.Decode(ApplicationEnvelope.Decode(envelope.Span, ReplicationProtocol.MaxApplicationBytes), maxChanges: 128);
        if (group.Dependencies.Count != 0)
        {
            throw new InvalidDataException("a repair page carries no dependency vector");
        }

        uint repaired = 0;
        var write = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (write.ConfigureAwait(false))
        {
            // A join changes nothing it already holds, so a repeated or stale page is harmless; no journal moves.
            foreach (var change in group.Changes)
            {
                if (await write.ApplyAsync(change, cancellationToken).ConfigureAwait(false))
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

    internal static IMemoryReservation Reserve(IMemoryBudget memory) => memory.TryReserve(MemoryLane.Bulk, WorkingBytes)
        ?? throw new RpcException(new Status(StatusCode.ResourceExhausted, "anti-entropy memory admission refused"));

    // Scan cursors may identify excluded keys, while transferred states must satisfy HasValidKey.
    internal static bool ValidCursor(string collection, string id) => collection.Length <= 63 && id.Length <= 1024
        && System.Text.Encoding.UTF8.GetByteCount(collection) <= 63 && System.Text.Encoding.UTF8.GetByteCount(id) <= 1024;
}
