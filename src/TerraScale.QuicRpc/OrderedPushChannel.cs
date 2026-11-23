using System.Buffers;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading.Channels;
using TerraScale.Contracts.Quic;

namespace TerraScale.QuicRpc;

internal sealed class PendingPush(byte[] buffer, int length)
{
    private const int Queued = 0;
    private const int Claimed = 1;
    private const int Abandoned = 2;

    private readonly TaskCompletionSource<Result<Done, QuicFault>> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;
    private byte[] _buffer = buffer;

    public ReadOnlyMemory<byte> Frame => _buffer.AsMemory(0, length);

    public int Length => length;

    public Task<Result<Done, QuicFault>> Completion => _completion.Task;

    public bool TryClaim() => Interlocked.CompareExchange(ref _state, Claimed, Queued) == Queued;

    public void Complete(Result<Done, QuicFault> result) => _completion.TrySetResult(result);

    public void Abandon(CallScope scope)
    {
        Interlocked.Exchange(ref _state, Abandoned);
        if (scope.Caller.IsCancellationRequested)
        {
            _completion.TrySetCanceled(scope.Caller);
            return;
        }

        _completion.TrySetResult(scope.Interrupted());
    }

    public void Release()
    {
        var rented = Interlocked.Exchange(ref _buffer, []);
        if (rented.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class OrderedPushChannel : IAsyncDisposable
{
    private readonly QuicConnectionPool _pool;
    private readonly PeerSlot _slot;
    private readonly Channel<PendingPush> _queue;
    private readonly CancellationTokenSource _stop;
    private StreamLease? _lease;
    private int _disposed;

    public OrderedPushChannel(QuicConnectionPool pool, PeerSlot slot)
    {
        _pool = pool;
        _slot = slot;
        _queue = Channel.CreateBounded<PendingPush>(new BoundedChannelOptions(QuicMeshLimits.OrderedPushQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _stop = CancellationTokenSource.CreateLinkedTokenSource(pool.ShutdownToken);
        Completion = PumpAsync();
    }

    public Task Completion { get; }

    public async ValueTask<Result<Done, QuicFault>> EnqueueAsync(PendingPush push, CallScope scope)
    {
        if (_queue.Writer.TryWrite(push))
        {
            return new Done();
        }

        try
        {
            while (await _queue.Writer.WaitToWriteAsync(scope.Token).ConfigureAwait(false))
            {
                if (_queue.Writer.TryWrite(push))
                {
                    return new Done();
                }
            }

            return new TransportShutdown("The ordered push channel is closed.");
        }
        catch (OperationCanceledException) when (!scope.Caller.IsCancellationRequested)
        {
            return scope.Interrupted();
        }
    }

    public void Complete() => _queue.Writer.TryComplete();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await Completion.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task PumpAsync()
    {
        while (await _queue.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            while (_queue.Reader.TryRead(out var push))
            {
                if (!push.TryClaim())
                {
                    push.Release();
                    continue;
                }

                var result = await DeliverAsync(push).ConfigureAwait(false);
                push.Complete(result);
                push.Release();
                if (result switch { Done => null, QuicFault fault => fault } is { } failure)
                {
                    FailQueued(failure);
                }
            }
        }

        await CloseStreamAsync().ConfigureAwait(false);
    }

    private void FailQueued(QuicFault failure)
    {
        while (_queue.Reader.TryRead(out var push))
        {
            if (push.TryClaim())
            {
                push.Complete(failure);
            }

            push.Release();
        }
    }

    private async ValueTask<Result<Done, QuicFault>> DeliverAsync(PendingPush push)
    {
        using var scope = new CallScope(QuicMeshLimits.CallTimeout, _pool.TimeProvider, _stop.Token, CancellationToken.None);
        if (_lease is { } lease)
        {
            if (ReferenceEquals(_slot.Current(), lease.Connection))
            {
                return await WriteAsync(lease, push, true, scope).ConfigureAwait(false);
            }

            Retire(lease);
        }

        return await OpenAndWriteAsync(push, scope).ConfigureAwait(false);
    }

    private async ValueTask<Result<Done, QuicFault>> OpenAndWriteAsync(PendingPush push, CallScope scope) =>
        await _pool.OpenStreamAsync(_slot, QuicStreamType.Unidirectional, scope).ConfigureAwait(false) switch
        {
            StreamLease opened => await WriteAsync(Track(opened), push, false, scope).ConfigureAwait(false),
            QuicFault fault => fault,
        };

    private StreamLease Track(StreamLease opened)
    {
        _lease = opened;
        _pool.Metrics.StreamOpened(QuicSide.Client);
        return opened;
    }

    private async ValueTask<Result<Done, QuicFault>> WriteAsync(StreamLease lease, PendingPush push, bool reused, CallScope scope) =>
        await QuicIo.WriteAsync(lease.Stream, push.Frame, false, scope).ConfigureAwait(false) switch
        {
            Done done => Sent(done, push.Length),
            QuicFault fault when reused && QuicFaults.IsStreamGone(fault) => await ReopenAsync(lease, fault, push, scope).ConfigureAwait(false),
            QuicFault fault => Reset(lease, fault),
        };

    private async ValueTask<Result<Done, QuicFault>> ReopenAsync(StreamLease lease, QuicFault fault, PendingPush push, CallScope scope)
    {
        _ = Reset(lease, fault);
        return await OpenAndWriteAsync(push, scope).ConfigureAwait(false);
    }

    private Done Sent(Done done, int length)
    {
        _pool.Metrics.Sent(QuicSide.Client, length);
        return done;
    }

    private QuicFault Reset(StreamLease lease, QuicFault fault)
    {
        _pool.Metrics.Error(QuicSide.Client, fault);
        Retire(lease);
        _pool.InvalidateIfDead(lease, fault);
        return fault;
    }

    private void Retire(StreamLease lease)
    {
        _lease = null;
        lease.Stream.Abort(QuicAbortDirection.Write, QuicProtocol.StreamErrorCode);
        _ = QuicIo.DisposeStreamAsync(lease.Stream);
        _pool.Metrics.StreamClosed(QuicSide.Client);
    }

    private async ValueTask CloseStreamAsync()
    {
        if (_lease is not { } lease)
        {
            return;
        }

        _lease = null;
        using var scope = new CallScope(QuicMeshLimits.DrainTimeout, _pool.TimeProvider, _stop.Token, CancellationToken.None);
        var completed = await QuicIo.WriteAsync(lease.Stream, ReadOnlyMemory<byte>.Empty, true, scope).ConfigureAwait(false);
        if (completed switch { Done => null, QuicFault fault => fault } is { } failure)
        {
            lease.Stream.Abort(QuicAbortDirection.Write, QuicProtocol.StreamErrorCode);
            _pool.Metrics.Error(QuicSide.Client, failure);
            _ = QuicIo.DisposeStreamAsync(lease.Stream);
        }
        else
        {
            await QuicIo.DisposeStreamAsync(lease.Stream).ConfigureAwait(false);
        }

        _pool.Metrics.StreamClosed(QuicSide.Client);
    }
}
