using Google.Protobuf;
using Grpc.Core;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Replication.V1;
using ZLogger;

namespace TerraBase.Features.Replication;

/// <summary>Bounds and timing of <c>CatalogReplication.Follow</c> (docs/decisions/0009).</summary>
internal static class CatalogProtocol
{
    public const uint Version = 1;

    /// <summary>Changes per page; one page is one envelope.</summary>
    public const int MaxPageChanges = 4096;

    /// <summary>Collection, id and JSON bytes after which a page ends: a page holds less plus one document of at most 1 MiB.</summary>
    public const int MaxPageBytes = 2 * 1024 * 1024;

    /// <summary>Most streams a primary serves at once.</summary>
    public const int MaxFollowers = 16;

    /// <summary>An idle primary sends its head at least this often.</summary>
    public static TimeSpan HeartbeatInterval { get; } = TimeSpan.FromSeconds(5);

    /// <summary>A follower ends a stream that sends nothing for this long.</summary>
    public static TimeSpan ProgressTimeout => ReplicationProtocol.ProgressTimeout;

    public static CatalogPage Page(IReadOnlyList<DocumentChange> changes) => new()
    {
        Codec = PayloadCodec.Lz4BlockV1,
        Envelope = UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(ChangeGroup.Encode(changes))),
    };

    /// <exception cref="InvalidDataException">The page is not a valid LZ4 envelope of a system change group.</exception>
    public static List<DocumentChange> Read(CatalogPage page) =>
        page.Codec == PayloadCodec.Lz4BlockV1
            ? ChangeGroup.Decode(
                ApplicationEnvelope.Decode(page.Envelope.Span, ReplicationProtocol.MaxApplicationBytes),
                MaxPageChanges, ReplicationProtocol.MaxApplicationBytes)
            : throw new InvalidDataException("a catalog page must use the LZ4 envelope");
}

/// <summary>
/// The catalog primary's side of <c>CatalogReplication.Follow</c>: a snapshot when the follower's frontier is not a prefix
/// of this journal, then every system database group, in pages, as it commits. No read transaction spans a network write.
/// </summary>
internal sealed class CatalogFeed(
    IStorageEngine engine, ISystemReplication system, TerraBaseOptions options, ILogger<CatalogFeed> logger, TimeProvider time,
    IHostApplicationLifetime lifetime)
    : CatalogReplication.CatalogReplicationBase
{
    private int _streams;

    public override async Task Follow(FollowRequest request, IServerStreamWriter<FollowResponse> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);
        if (options.CatalogPrimary is not null)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "this member is not a catalog primary"));
        }

        if (request.Protocol != CatalogProtocol.Version || !request.Codecs.Contains(PayloadCodec.Lz4BlockV1))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"the follower needs catalog protocol {CatalogProtocol.Version} with the LZ4 envelope"));
        }

        try
        {
            if (Interlocked.Increment(ref _streams) > CatalogProtocol.MaxFollowers)
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "too many catalog followers"));
            }

            var region = TerraBaseOptions.IsRegion(request.Region) ? request.Region : "unknown";
            logger.ZLogInformation($"A catalog follower in region {region:@Region} connected");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
            await ServeAsync(request, responseStream, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _streams);
        }
    }

    private async Task ServeAsync(FollowRequest request, IServerStreamWriter<FollowResponse> responses, CancellationToken cancellationToken)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var (origin, head) = await HeadAsync(store, cancellationToken).ConfigureAwait(false);
        await responses.WriteAsync(new FollowResponse
        {
            Welcome = new CatalogWelcome
            {
                Protocol = CatalogProtocol.Version,
                Origin = UnsafeByteOperations.UnsafeWrap(origin),
                Head = (ulong)head,
                Region = options.Region,
            },
        }, cancellationToken).ConfigureAwait(false);

        var sent = await ResumesAsync(request, origin, head, cancellationToken).ConfigureAwait(false)
            ? (long)request.Sequence
            : await SendSnapshotAsync(store, responses, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            // Read the commit counter before the head, so a commit after the read still ends the wait.
            var observed = store.CommitSequence;
            (_, head) = await HeadAsync(store, cancellationToken).ConfigureAwait(false);
            for (; sent < head; sent++)
            {
                await SendGroupAsync(sent + 1, responses, cancellationToken).ConfigureAwait(false);
            }

            await responses.WriteAsync(new FollowResponse { Heartbeat = new CatalogHeartbeat { Head = (ulong)sent } }, cancellationToken)
                .ConfigureAwait(false);
            // The wait ends with its own token, so an idle primary leaves no registration behind per heartbeat.
            using var heartbeat = new CancellationTokenSource(CatalogProtocol.HeartbeatInterval, time);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, heartbeat.Token);
            try
            {
                await store.WaitForCommitAsync(observed, idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Idle: the next heartbeat tells the follower the stream is alive.
            }
        }
    }

    // Whether the follower's frontier is a prefix of this journal (the same origin, and the same digest at its sequence)
    // that the retained groups continue. Sequence 0 proves nothing about the documents written before capture began (a
    // file restored under the same origin has the same empty prefix), so it always gets a snapshot.
    private async ValueTask<bool> ResumesAsync(FollowRequest request, ReadOnlyMemory<byte> origin, long head, CancellationToken cancellationToken)
    {
        if (!request.Origin.Span.SequenceEqual(origin.Span) || request.Sequence is 0 || request.Sequence > (ulong)head
            || request.Digest.Length != JournalDigest.Length)
        {
            return false;
        }

        var sequence = (long)request.Sequence;
        return await DigestAsync(sequence, cancellationToken).ConfigureAwait(false) is { } digest
            && digest.Span.SequenceEqual(request.Digest.Span)
            && (sequence == head || (await system.ReadGroupAsync(sequence + 1, 0, 1, 1, cancellationToken).ConfigureAwait(false)).Count > 0);
    }

    // Live documents in fuzzy pages, then the groups committed since the snapshot began: replaying them over any page
    // state gives the state at the last one, which the follower commits atomically.
    private async Task<long> SendSnapshotAsync(IDatabaseStore store, IServerStreamWriter<FollowResponse> responses, CancellationToken cancellationToken)
    {
        var (_, start) = await HeadAsync(store, cancellationToken).ConfigureAwait(false);
        await responses.WriteAsync(new FollowResponse
        {
            SnapshotBegin = new SnapshotBegin
            {
                Sequence = (ulong)start,
                Digest = UnsafeByteOperations.UnsafeWrap(await RequireDigestAsync(start, cancellationToken).ConfigureAwait(false)),
            },
        }, cancellationToken).ConfigureAwait(false);

        SystemDocumentPage page;
        (string Collection, string Id)? after = null;
        do
        {
            page = await system.ReadDocumentsAsync(
                after?.Collection, after?.Id, CatalogProtocol.MaxPageChanges, CatalogProtocol.MaxPageBytes, cancellationToken).ConfigureAwait(false);
            if (page.Documents.Count > 0)
            {
                await responses.WriteAsync(new FollowResponse { Documents = CatalogProtocol.Page(page.Documents) }, cancellationToken).ConfigureAwait(false);
                after = (page.Documents[^1].Collection, page.Documents[^1].Id);
            }
        }
        while (page.HasMore);

        var (_, end) = await HeadAsync(store, cancellationToken).ConfigureAwait(false);
        for (var sequence = start + 1; sequence <= end; sequence++)
        {
            await SendGroupAsync(sequence, responses, cancellationToken).ConfigureAwait(false);
        }

        var digest = await RequireDigestAsync(end, cancellationToken).ConfigureAwait(false);
        long offset;
        var read = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (read.ConfigureAwait(false))
        {
            // Read after the head, so it covers the version of every group sent; deletions leave no version in the pages.
            offset = await read.GetOffsetAsync(cancellationToken).ConfigureAwait(false);
        }

        await responses.WriteAsync(new FollowResponse
        {
            SnapshotEnd = new SnapshotEnd { Sequence = (ulong)end, Digest = UnsafeByteOperations.UnsafeWrap(digest), CommitOffset = (ulong)offset },
        }, cancellationToken).ConfigureAwait(false);
        return end;
    }

    private async Task SendGroupAsync(long sequence, IServerStreamWriter<FollowResponse> responses, CancellationToken cancellationToken)
    {
        var sent = 0;
        var page = await system.ReadGroupAsync(sequence, sent, CatalogProtocol.MaxPageChanges, CatalogProtocol.MaxPageBytes, cancellationToken)
            .ConfigureAwait(false);
        if (page.Count == 0)
        {
            throw new InvalidDataException("the system journal is missing a group below its head");
        }

        await responses.WriteAsync(new FollowResponse { GroupBegin = new GroupBegin { Sequence = (ulong)sequence, ChangeCount = (uint)page.Count } }, cancellationToken)
            .ConfigureAwait(false);
        while (true)
        {
            await responses.WriteAsync(new FollowResponse { Changes = CatalogProtocol.Page(page.Changes) }, cancellationToken).ConfigureAwait(false);
            sent += page.Changes.Count;
            if (sent >= page.Count)
            {
                break;
            }

            page = await system.ReadGroupAsync(sequence, sent, CatalogProtocol.MaxPageChanges, CatalogProtocol.MaxPageBytes, cancellationToken)
                .ConfigureAwait(false);
            if (page.Changes.Count == 0)
            {
                // Truncated while it was sent: the follower reconnects and gets a snapshot.
                throw new InvalidDataException("a catalog group left the retained journal tail while it was sent");
            }
        }

        await responses.WriteAsync(new FollowResponse
        {
            GroupEnd = new GroupEnd { Digest = UnsafeByteOperations.UnsafeWrap(await RequireDigestAsync(sequence, cancellationToken).ConfigureAwait(false)) },
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<(ReadOnlyMemory<byte> Origin, long Head)> HeadAsync(IDatabaseStore store, CancellationToken cancellationToken)
    {
        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return (await transaction.GetOriginAsync(cancellationToken).ConfigureAwait(false),
                await transaction.GetLastChangeSequenceAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    private ValueTask<ReadOnlyMemory<byte>?> DigestAsync(long sequence, CancellationToken cancellationToken) =>
        system.GetDigestAsync(sequence, cancellationToken);

    private async ValueTask<ReadOnlyMemory<byte>> RequireDigestAsync(long sequence, CancellationToken cancellationToken) =>
        await DigestAsync(sequence, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException("the system journal is missing a prefix digest below its head");
}
