using Google.Protobuf;
using Grpc.Core;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.Replication.V1;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Primitives;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Replication;

/// <summary>
/// Receiving side of <c>Replication.Sync</c> (see <see cref="ReplicationProtocol"/>). Groups are applied one at a
/// time through <see cref="IReplicationJournal"/>, so a sender is held back by its in-flight window until each
/// group is durable; each applied or duplicate group is answered with <c>DurableAck</c> and <c>AppliedAck</c>.
/// An invalid, unsupported or out-of-order group ends the stream with a <c>StreamError</c> and applies nothing.
/// </summary>
internal sealed partial class ReplicationService(
    IStorageEngine engine, IReplicationJournal journal, ITenantDirectory tenants, ReplicationMetrics metrics, TerraBaseOptions options, ILogger<ReplicationService> logger,
    DatabaseLifecycleCheckpoint checkpoint)
    : V1.Replication.ReplicationBase
{
    // One instance per node (registered as a singleton), so this bounds the node's open streams.
    private int _streams;

    public override async Task Sync(
        IAsyncStreamReader<SyncRequest> requestStream,
        IServerStreamWriter<SyncResponse> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var stream = new Stream(requestStream, responseStream, lifetime);
            if (Interlocked.Increment(ref _streams) > ReplicationProtocol.MaxOpenStreams)
            {
                await stream.SendAsync(Error(StreamErrorCode.StreamErrorResourceExhausted, retryable: true)).ConfigureAwait(false);
                return;
            }

            await ServeAsync(stream, lifetime, context.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _streams);
        }
    }

    private async Task ServeAsync(Stream stream, CancellationTokenSource lifetime, CancellationToken cancellationToken)
    {
        if (await stream.NextAsync().ConfigureAwait(false) is not { FrameCase: SyncRequest.FrameOneofCase.Hello } first)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "the first Sync request must be Hello"));
        }

        var refused = ReplicationProtocol.Accept(first.Hello, out var database, out var origin, out var limits);
        if (refused != StreamErrorCode.StreamErrorUnspecified)
        {
            await stream.SendAsync(Error(refused, retryable: false)).ConfigureAwait(false);
            return;
        }

        stream.MaxMessageBytes = limits.MaxGrpcMessageBytes;

        // Refuse limits that cannot carry the largest required Welcome or acknowledgment.
        var maximumMember = ReplicationProtocol.Member(database, origin, options.Region);
        var initialProof = JournalDigest.Initial(origin.Span);
        var maximumWelcome = Welcome(maximumMember, origin, long.MaxValue, initialProof, limits);
        var maximumAck = new SyncResponse
        {
            Durable = new DurableAck
            {
                Context = new AckContext
                {
                    AcknowledgingPeer = maximumMember,
                    EventId = new CommittedEventId
                    {
                        TenantId = maximumMember.TenantId,
                        ProjectId = maximumMember.ProjectId,
                        DatabaseId = maximumMember.DatabaseId,
                        Origin = ReplicationProtocol.Origin(origin),
                        Sequence = long.MaxValue,
                    },
                },
                DurablePrefix = long.MaxValue,
                JournalDigest = ByteString.CopyFrom(initialProof),
            },
        };
        if (Math.Max(maximumWelcome.CalculateSize(), maximumAck.CalculateSize()) > limits.MaxGrpcMessageBytes)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "the negotiated gRPC cap cannot carry required control responses"));
        }

        // A member that replicates with others receives only databases its catalog registers: a deleted one is not recreated,
        // and a catalog follower takes a new one once its catalog has it. The sender retries. A deletion committed between the
        // check and the open is caught by checking again: a store other than the one open before the check is the open's own
        // incarnation, which goes too once the catalog confirms the database is unregistered.
        var gated = options.PeerAddresses.Count > 0 || options.CatalogPrimary is not null;
        var before = gated ? await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) : null;
        if (gated && await tenants.RegisteredAsync(database, cancellationToken).ConfigureAwait(false) is not true)
        {
            await stream.SendAsync(Error(StreamErrorCode.StreamErrorUnauthorized, retryable: true)).ConfigureAwait(false);
            return;
        }

        checkpoint("replication.registered", database);
        // The receiving database's own origin identifies this node on the stream, in Welcome and in every acknowledgment.
        var (store, receiver, applied, digest) = await journal.OpenAsync(database, origin, cancellationToken).ConfigureAwait(false);
        if (gated && await tenants.RegisteredAsync(database, cancellationToken).ConfigureAwait(false) is var registered && registered is not true)
        {
            if (registered is false && !ReferenceEquals(before, store))
            {
                _ = await engine.DeleteDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
            }

            await stream.SendAsync(Error(StreamErrorCode.StreamErrorUnauthorized, retryable: true)).ConfigureAwait(false);
            return;
        }

        void OnDeleted(object? sender, IDatabaseStore deleted)
        {
            if (ReferenceEquals(store, deleted))
            {
                lifetime.Cancel();
            }
        }

        var member = ReplicationProtocol.Member(database, receiver, options.Region);
        // Pin the incarnation through Welcome, duplicate checks and ACKs. Retirement cancels even an idle
        // stream; a new stream must read the recreated database's journal before it can acknowledge anything.
        engine.DatabaseDeleted += OnDeleted;
        try
        {
            ObjectDisposedException.ThrowIf(store.IsClosed, store);
            await stream.SendAsync(Welcome(member, origin, applied, digest, limits)).ConfigureAwait(false);
            LogPeerRegion(logger, options.Region, first.Hello.LocalIdentity.Region is "" ? "unknown" : first.Hello.LocalIdentity.Region);
            while (await stream.NextAsync().ConfigureAwait(false) is { } request)
            {
                switch (request.FrameCase)
                {
                    case SyncRequest.FrameOneofCase.CommittedGroup:
                        var batch = request.CommittedGroup;
                        var (prefix, proof, invalid) = await ApplyAsync(request, store, origin, limits, applied, digest, lifetime.Token).ConfigureAwait(false);
                        if (invalid != StreamErrorCode.StreamErrorUnspecified)
                        {
                            await stream.SendAsync(Error(invalid, retryable: false)).ConfigureAwait(false);
                            return;
                        }

                        applied = prefix;
                        digest = proof;
                        if (applied < (long)batch.EventId.Sequence)
                        {
                            // A gap: the sender skipped a group. It resumes after the prefix on its next stream.
                            await stream.SendAsync(Error(StreamErrorCode.StreamErrorInvalidBatch, retryable: true)).ConfigureAwait(false);
                            return;
                        }

                        var context = new AckContext
                        {
                            AcknowledgingPeer = member,
                            MembershipEpoch = member.MembershipEpoch,
                            EventId = new CommittedEventId
                            {
                                TenantId = member.TenantId,
                                ProjectId = member.ProjectId,
                                DatabaseId = member.DatabaseId,
                                Origin = ReplicationProtocol.Origin(origin),
                                Sequence = batch.EventId.Sequence,
                            },
                        };
                        ObjectDisposedException.ThrowIf(store.IsClosed, store);
                        await stream.SendAsync(new SyncResponse { Durable = new DurableAck { Context = context, DurablePrefix = (ulong)applied, JournalDigest = ByteString.CopyFrom(digest.Span) } }).ConfigureAwait(false);
                        await stream.SendAsync(new SyncResponse { Applied = new AppliedAck { Context = context, AppliedPrefix = (ulong)applied, JournalDigest = ByteString.CopyFrom(digest.Span) } }).ConfigureAwait(false);
                        break;
                    case SyncRequest.FrameOneofCase.KeepAlive:
                        await stream.SendAsync(new SyncResponse { KeepAlive = request.KeepAlive }).ConfigureAwait(false);
                        break;
                    case SyncRequest.FrameOneofCase.Credit:
                        break;
                    default:
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "Hello may only open a Sync stream"));
                }
            }
        }
        catch (Exception) when (store.IsClosed && !cancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "the receiving database was deleted; reopen the Sync stream"));
        }
        finally
        {
            engine.DatabaseDeleted -= OnDeleted;
        }
    }

    // The applied prefix after the batch, or why the batch is refused.
    private async Task<(long Prefix, ReadOnlyMemory<byte> Digest, StreamErrorCode Invalid)> ApplyAsync(
        SyncRequest request, IDatabaseStore store, ReadOnlyMemory<byte> origin, TransferLimits limits, long applied, ReadOnlyMemory<byte> digest, CancellationToken cancellationToken)
    {
        var batch = request.CommittedGroup;
        List<DocumentChange> changes;
        try
        {
            changes = ReplicationProtocol.Read(request, store.Tenant ?? throw new InvalidOperationException("system databases cannot replicate"), origin, limits);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            metrics.Received("rejected");
            return (applied, digest, error is NotSupportedException ? StreamErrorCode.StreamErrorUnsupportedFormat : StreamErrorCode.StreamErrorInvalidBatch);
        }

        var sequence = (long)batch.EventId.Sequence;
        // The persisted prefix is consulted for duplicates too; stream-local state is no durability proof.
        (long Sequence, ReadOnlyMemory<byte> Digest) prefix;
        try
        {
            prefix = await journal.AppendAsync(store, origin, sequence, changes, batch.PreviousJournalDigest.Memory, batch.JournalDigest.Memory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidDataException or DocumentValidationException)
        {
            metrics.Received("rejected");
            return (applied, digest, StreamErrorCode.StreamErrorInvalidBatch);
        }

        metrics.Received(sequence <= applied ? "duplicate" : prefix.Sequence >= sequence ? "applied" : "rejected");
        return (prefix.Sequence, prefix.Digest, StreamErrorCode.StreamErrorUnspecified);
    }

    private static SyncResponse Welcome(PeerIdentity member, ReadOnlyMemory<byte> origin, long applied, ReadOnlyMemory<byte> digest, TransferLimits limits)
    {
        var frontier = new PeerFrontier { Origin = ReplicationProtocol.Origin(origin), JournalDigest = ByteString.CopyFrom(digest.Span) };
        if (applied > 0)
        {
            frontier.ReceivedSequence = frontier.DurableSequence = frontier.AppliedSequence = (ulong)applied;
        }

        return new SyncResponse
        {
            Welcome = new Welcome
            {
                LocalIdentity = member,
                ProtocolVersion = new ProtocolVersion { Major = 1 },
                EventFormatVersion = ReplicationProtocol.FormatVersion,
                RecordFormatVersion = ReplicationProtocol.FormatVersion,
                MergePolicyVersion = ReplicationProtocol.FormatVersion,
                Codecs = { PayloadCodec.Lz4BlockV1 },
                Limits = limits,
                Frontiers = { frontier },
            },
        };
    }

    private static SyncResponse Error(StreamErrorCode code, bool retryable) =>
        new() { Error = new StreamError { Code = code, Retryable = retryable } };

    [LoggerMessage(LogLevel.Information, "Replication receiving in region {LocalRegion} from peer region {PeerRegion}")]
    private static partial void LogPeerRegion(ILogger logger, string localRegion, string peerRegion);

    // Reads requests and writes responses, ending the call when either waits longer than the idle timeout:
    // a sender that stops sending, or stops reading so that a response cannot be written, holds no stream slot forever.
    private sealed class Stream(
        IAsyncStreamReader<SyncRequest> requests, IServerStreamWriter<SyncResponse> responses,
        CancellationTokenSource lifetime)
    {
        public uint MaxMessageBytes { get; set; } = ReplicationProtocol.MaxMessageBytes;

        public async Task<SyncRequest?> NextAsync()
        {
            var request = await WithinIdleTimeoutAsync(async token => await requests.MoveNext(token).ConfigureAwait(false) ? requests.Current : null).ConfigureAwait(false);
            if (request?.CalculateSize() > MaxMessageBytes && request.CommittedGroup is null)
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "the request exceeds the negotiated gRPC message size"));
            }

            return request;
        }

        public async Task SendAsync(SyncResponse response)
        {
            if (response.CalculateSize() > MaxMessageBytes)
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "the response exceeds the negotiated gRPC message size"));
            }

            await WithinIdleTimeoutAsync(async token =>
            {
                await responses.WriteAsync(response, token).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }

        private async Task<T> WithinIdleTimeoutAsync<T>(Func<CancellationToken, Task<T>> wait)
        {
            using var operation = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource();
            var cause = 0;
            void Cancel(int reason)
            {
                // Record the first cause before canceling I/O: a timed-out write may abort the parent context too.
                if (Interlocked.CompareExchange(ref cause, reason, 0) == 0)
                {
                    operation.Cancel();
                }
            }

            using var parent = lifetime.Token.Register(() => Cancel(1));
            using var expired = timeout.Token.Register(() => Cancel(2));
            timeout.CancelAfter(ReplicationProtocol.IdleTimeout);
            try
            {
                return await wait(operation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref cause) == 2)
            {
                throw new RpcException(new Status(StatusCode.DeadlineExceeded, "the peer made no progress on the Sync stream within the idle timeout"));
            }
        }
    }
}
