using System.Buffers;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading.Channels;
using TerraScale.Contracts.Quic;

namespace TerraScale.QuicRpc;

internal sealed record PushEnqueued;

internal sealed record ChannelEvicted;

internal readonly union PushEnqueueOutcome(PushEnqueued, ChannelEvicted, QuicFault);

internal readonly union PushDeliveryOutcome(QuicSent, QuicFault);

internal sealed class PendingPush(byte[] buffer, int length)
{
    private const int Queued = 0;
    private const int Claimed = 1;
    private const int Abandoned = 2;

    private readonly TaskCompletionSource<PushDeliveryOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;
    private byte[] _buffer = buffer;

    public ReadOnlyMemory<byte> Frame => _buffer.AsMemory(0, length);

    public int Length => length;

    public Task<PushDeliveryOutcome> Completion => _completion.Task;

    public bool TryClaim() => Interlocked.CompareExchange(ref _state, Claimed, Queued) == Queued;

    public void Complete(PushDeliveryOutcome result) => _completion.TrySetResult(result);

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
    private readonly string _key;
    private readonly Channel<PendingPush> _queue;
    private readonly Lock _enqueueGate = new();
    private readonly Lock _stopGate = new();
    private readonly CancellationTokenSource _stop;
    private StreamLease? _lease;
    private bool _stopDisposed;
    private int _evicted;
    private int _disposed;

    public OrderedPushChannel(QuicConnectionPool pool, PeerSlot slot, string key)
    {
        _pool = pool;
        _slot = slot;
        _key = key;
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

    public async ValueTask<PushEnqueueOutcome> EnqueueAsync(PendingPush push, CallScope scope)
    {
        while (true)
        {
            lock (_enqueueGate)
            {
                if (_queue.Writer.TryWrite(push))
                {
                    return new PushEnqueued();
                }
            }

            var wait = _queue.Writer.WaitToWriteAsync(scope.Token).AsTask();
            await ((Task)wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (wait.IsCanceled && !scope.Caller.IsCancellationRequested)
            {
                return scope.Interrupted();
            }

            if (!await wait)
            {
                return Volatile.Read(ref _evicted) != 0
                    ? new ChannelEvicted()
                    : new TransportShutdown("The ordered push channel is closed.");
            }
        }
    }

    public void Complete() => _queue.Writer.TryComplete();

    public bool TryCompleteIdle()
    {
        lock (_enqueueGate)
        {
            if (_queue.Reader.TryPeek(out _))
            {
                return false;
            }

            Volatile.Write(ref _evicted, 1);
            _queue.Writer.TryComplete();
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        CancelStop();
        await Completion;
        DisposeStop();
    }

    private void CancelStop()
    {
        lock (_stopGate)
        {
            if (!_stopDisposed)
            {
                _stop.Cancel();
            }
        }
    }

    private void DisposeStop()
    {
        lock (_stopGate)
        {
            if (_stopDisposed)
            {
                return;
            }

            _stopDisposed = true;
            _stop.Dispose();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                if (!_queue.Reader.TryPeek(out _) && !await WaitForItemAsync())
                {
                    break;
                }

                while (_queue.Reader.TryRead(out var push))
                {
                    if (!push.TryClaim())
                    {
                        push.Release();
                        continue;
                    }

                    var result = await DeliverAsync(push);
                    push.Complete(result);
                    push.Release();
                    if (result switch { QuicSent => null, QuicFault fault => fault } is { } failure)
                    {
                        FailQueued(failure);
                    }
                }
            }

            await CloseStreamAsync();
        }
        finally
        {
            if (Volatile.Read(ref _evicted) != 0)
            {
                DisposeStop();
            }
        }
    }

    private async ValueTask<bool> WaitForItemAsync()
    {
        using var idleCancel = new CancellationTokenSource();
        var waitTask = _queue.Reader.WaitToReadAsync().AsTask();
        var idleTask = Task.Delay(QuicMeshLimits.OrderedChannelIdleTimeout, _pool.TimeProvider, idleCancel.Token);
        var winner = await Task.WhenAny(waitTask, idleTask);
        await idleCancel.CancelAsync();
        if (winner == idleTask)
        {
            _slot.EvictIdleChannel(_key, this);
        }

        return await waitTask;
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

    private async ValueTask<PushDeliveryOutcome> DeliverAsync(PendingPush push)
    {
        using var scope = new CallScope(QuicMeshLimits.CallTimeout, _pool.TimeProvider, _stop.Token, CancellationToken.None);
        if (_lease is { } lease)
        {
            if (ReferenceEquals(_slot.Current(), lease.Connection))
            {
                return await WriteAsync(lease, push, true, scope);
            }

            Retire(lease);
        }

        return await OpenAndWriteAsync(push, scope);
    }

    private async ValueTask<PushDeliveryOutcome> OpenAndWriteAsync(PendingPush push, CallScope scope)
    {
        var outcome = await _pool.OpenStreamAsync(_slot, QuicStreamType.Unidirectional, scope);
        return outcome switch
        {
            StreamLease opened => await WriteAsync(Track(opened), push, false, scope),
            QuicFault fault => fault,
        };
    }

    private StreamLease Track(StreamLease opened)
    {
        _lease = opened;
        _pool.Metrics.StreamOpened(QuicSide.Client);
        return opened;
    }

    private async ValueTask<PushDeliveryOutcome> WriteAsync(StreamLease lease, PendingPush push, bool reused, CallScope scope)
    {
        var outcome = await QuicIo.WriteAsync(lease.Stream, push.Frame, false, scope);
        return outcome switch
        {
            QuicSent done => Sent(done, push.Length),
            QuicFault fault when reused && QuicFaults.IsStreamGone(fault) => await ReopenAsync(lease, fault, push, scope),
            QuicFault fault => Reset(lease, fault),
        };
    }

    private async ValueTask<PushDeliveryOutcome> ReopenAsync(StreamLease lease, QuicFault fault, PendingPush push, CallScope scope)
    {
        _ = Reset(lease, fault);
        return await OpenAndWriteAsync(push, scope);
    }

    private QuicSent Sent(QuicSent done, int length)
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
        _ = lease.ReleaseAsync();
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
        var completed = await QuicIo.WriteAsync(lease.Stream, ReadOnlyMemory<byte>.Empty, true, scope);
        if (completed switch { QuicSent => null, QuicFault fault => fault } is { } failure)
        {
            lease.Stream.Abort(QuicAbortDirection.Write, QuicProtocol.StreamErrorCode);
            _pool.Metrics.Error(QuicSide.Client, failure);
            _pool.InvalidateIfDead(lease, failure);
            _ = lease.ReleaseAsync();
        }
        else
        {
            await lease.ReleaseAsync();
        }

        _pool.Metrics.StreamClosed(QuicSide.Client);
    }
}
