using Google.Protobuf;
using Grpc.Core;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication.Alpha;

namespace TerraBase.Features.Replication;

internal sealed class AntiEntropyService(IAntiEntropyState state, IMemoryBudget memory, TimeProvider time) : Alpha.AntiEntropy.AntiEntropyBase
{
    private int _calls;

    public override Task<MerkleDigest> Digest(ShardIdentity request, ServerCallContext context) => ServeAsync(async token =>
    {
        var store = await state.OpenAsync(request, token).ConfigureAwait(false);
        ByteString origin;
        JournalProof? applied = null;
        var read = await store.BeginTransactionAsync(write: false, token).ConfigureAwait(false);
        await using (read.ConfigureAwait(false))
        {
            origin = ByteString.CopyFrom((await read.GetOriginAsync(token).ConfigureAwait(false)).Span);
            if (!request.Origin.IsEmpty)
            {
                var (sequence, frontierDigest) = await read.GetAppliedFrontierAsync(request.Origin.Memory, token).ConfigureAwait(false);
                applied = new JournalProof { Origin = request.Origin, Sequence = (ulong)sequence, Digest = ByteString.CopyFrom(frontierDigest.Span) };
            }
        }

        var digest = await state.DigestAsync(store, token).ConfigureAwait(false);
        return new MerkleDigest
        {
            Incarnation = ByteString.CopyFrom(store.Incarnation.ToByteArray(bigEndian: true)),
            Envelope = ByteString.CopyFrom(ApplicationEnvelope.Encode(digest).Span),
            Origin = origin,
            AppliedSource = applied,
        };
    }, context, AntiEntropyState.DigestTimeout);

    public override Task<StatePage> ReadRange(RangeRequest request, ServerCallContext context) => ServeAsync(async token =>
    {
        RequireIncarnation(request.Identity);
        var store = await state.OpenAsync(request.Identity, token).ConfigureAwait(false);
        return await state.ReadAsync(store, request, token).ConfigureAwait(false);
    }, context);

    public override Task<MergeResult> Merge(StateRequest request, ServerCallContext context) => ServeAsync(async token =>
    {
        RequireIncarnation(request.Identity);
        var store = await state.OpenAsync(request.Identity, token).ConfigureAwait(false);
        return new MergeResult { RepairedKeys = await state.MergeAsync(store, request.Identity, request.Envelope, token).ConfigureAwait(false) };
    }, context);

    private async Task<T> ServeAsync<T>(Func<CancellationToken, Task<T>> work, ServerCallContext context, TimeSpan? budget = null)
    {
        ResponseAdmission? admission = null;
        var transferred = false;
        try
        {
            if (Interlocked.Increment(ref _calls) > ReplicationProtocol.MaxOpenStreams)
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "too many anti-entropy calls"));
            }

            admission = new ResponseAdmission(this, memory, context, time, budget ?? ReplicationProtocol.ProgressTimeout);
            // A unary handler returns before gRPC writes its response. Keep admission through the
            // response completion, including cancellation, so a slow reader cannot accumulate pages.
            context.GetHttpContext().Response.OnCompleted(static pending =>
            {
                ((ResponseAdmission)pending).Dispose();
                return Task.CompletedTask;
            }, admission);
            transferred = true;
            context.GetHttpContext().Features.Set(admission);
            var response = await work(admission.Token).ConfigureAwait(false);
            admission.Token.ThrowIfCancellationRequested();
            return response;
        }
        catch (InvalidDataException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
        finally
        {
            if (transferred)
            {
                admission!.BeginResponse();
            }
            else
            {
                if (admission is null)
                {
                    Interlocked.Decrement(ref _calls);
                }
                else
                {
                    admission.Dispose();
                }
            }
        }
    }

    internal static async Task FlushResponseAsync(HttpContext context)
    {
        if (context.Features.Get<ResponseAdmission>() is not { } admission)
        {
            return;
        }

        try
        {
            // Unary gRPC queues its serialized body without flushing. Keep backpressure inside admission.
            var flushed = await context.Response.BodyWriter.FlushAsync(admission.Token).ConfigureAwait(false);
            if (flushed.IsCanceled)
            {
                context.Abort();
            }

            admission.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (admission.Token.IsCancellationRequested)
        {
            context.Abort();
        }
    }

    private sealed class ResponseAdmission : IDisposable
    {
        private readonly AntiEntropyService _service;
        private readonly IMemoryReservation _reservation;
        private readonly CancellationTokenSource _timeout;
        private readonly CancellationTokenSource _deadline;
        private readonly CancellationTokenRegistration _abort;
        private int _released;

        public ResponseAdmission(AntiEntropyService service, IMemoryBudget memory, ServerCallContext context, TimeProvider time, TimeSpan budget)
        {
            _service = service;
            var request = context.GetHttpContext();
            _timeout = new CancellationTokenSource(budget, time);
            try
            {
                _deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _timeout.Token);
                _reservation = AntiEntropyState.Reserve(memory);
                _abort = _timeout.Token.Register(static request => ((HttpContext)request!).Abort(), request);
            }
            catch
            {
                _deadline?.Dispose();
                _reservation?.Dispose();
                _timeout.Dispose();
                throw;
            }
        }

        public CancellationToken Token => _deadline.Token;

        // Keep the deadline alive while gRPC serializes and writes, even after a long digest.
        public void BeginResponse() => _timeout.CancelAfter(ReplicationProtocol.ProgressTimeout);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                try
                {
                    _abort.Dispose();
                    _deadline.Dispose();
                    _timeout.Dispose();
                }
                finally
                {
                    try
                    {
                        _reservation.Dispose();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _service._calls);
                    }
                }
            }
        }
    }

    private static void RequireIncarnation(ShardIdentity? identity)
    {
        if (identity?.Incarnation.Length != 16)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "repair requires a database incarnation"));
        }
    }
}
