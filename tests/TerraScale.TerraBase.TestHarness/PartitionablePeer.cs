using Grpc.Core;
using Grpc.Net.Client;
using TerraBase.Features.Replication.V1;

namespace TerraBase.Testing;

// One directed link. All envelopes and acknowledgements come from the real nodes.
internal sealed class PartitionablePeer : IAsyncDisposable
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private readonly Lock _gate = new();
    private readonly HashSet<TaskCompletionSource> _streams = [];
    private readonly GrpcChannel _channel;
    private CancellationTokenSource _generation = new();
    private FakePeer? _listener;
    private TaskCompletionSource? _nextBatch;
    private bool _cut;
    private Task<int>? _cutCompletion;
    private Task? _cleanupCompletion;

    private PartitionablePeer(PeerTls tls, Uri target)
    {
        _channel = tls.CreateChannel(target);
    }

    public Uri Address => _listener!.Address;

    public Task CleanupCompletion
    {
        get
        {
            lock (_gate)
            {
                return _cleanupCompletion ??= CleanupAsync();
            }
        }
    }

    public static async Task<PartitionablePeer> StartAsync(PeerTls tls, Uri target)
    {
        var link = new PartitionablePeer(tls, target);
        try
        {
            link._listener = await FakePeer.StartAsync(tls, link.ForwardAsync);
            return link;
        }
        catch
        {
            link._channel.Dispose();
            link._generation.Dispose();
            throw;
        }
    }

    public Task ObserveNextBatch()
    {
        lock (_gate)
        {
            _nextBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _nextBatch.Task;
        }
    }

    public Task<int> CutAsync(CancellationToken token) => BeginCut().WaitAsync(token);

    private Task<int> BeginCut()
    {
        lock (_gate)
        {
            _cut = true;
            return _cutCompletion ??= JoinCutAsync(_generation, [.. _streams.Select(stream => stream.Task)]);
        }
    }

    private static async Task<int> JoinCutAsync(CancellationTokenSource generation, Task[] active)
    {
        try
        {
            await generation.CancelAsync();
        }
        finally
        {
            await Task.WhenAll(active);
        }

        return active.Length;
    }

    public void Heal()
    {
        lock (_gate)
        {
            if (_cleanupCompletion is not null || _cutCompletion?.IsCompletedSuccessfully is not true)
            {
                throw new InvalidOperationException("the relay can heal only after its cut finished successfully");
            }

            _generation.Dispose();
            _generation = new();
            _cutCompletion = null;
            _cut = false;
        }
    }

    private async Task ForwardAsync(IAsyncStreamReader<SyncRequest> requests, IServerStreamWriter<SyncResponse> responses,
        ServerCallContext context)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource stop;
        lock (_gate)
        {
            if (_cut)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "test partition"));
            }

            if (_streams.Count >= 16)
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "test relay stream limit"));
            }

            stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _generation.Token);
            _streams.Add(done);
        }

        try
        {
            using var lifetime = stop;
            using var call = new Features.Replication.V1.Replication.ReplicationClient(_channel).Sync(cancellationToken: stop.Token);
            var sending = SendAsync();
            var receiving = ReceiveAsync();
            try
            {
                await Task.WhenAll(sending, receiving);
            }
            finally
            {
                await stop.CancelAsync();
            }

            async Task SendAsync()
            {
                try
                {
                    while (await requests.MoveNext(stop.Token))
                    {
                        await call.RequestStream.WriteAsync(requests.Current, stop.Token);
                        if (requests.Current.CommittedGroup is not null)
                        {
                            lock (_gate)
                            {
                                _nextBatch?.TrySetResult();
                                _nextBatch = null;
                            }
                        }
                    }

                    await call.RequestStream.CompleteAsync();
                }
                catch
                {
                    await stop.CancelAsync();
                    throw;
                }
            }

            async Task ReceiveAsync()
            {
                try
                {
                    while (await call.ResponseStream.MoveNext(stop.Token))
                    {
                        await responses.WriteAsync(call.ResponseStream.Current, stop.Token);
                    }
                }
                finally
                {
                    // A receiver can end the call while the source is still reading requests.
                    await stop.CancelAsync();
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _streams.Remove(done);
            }

            done.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(CleanupTimeout);
        await TerraBaseTestServer.WaitForCleanupAsync(CleanupCompletion, TerraBaseTestServer.ReportCleanupFailure, deadline.Token);
    }

    private async Task CleanupAsync()
    {
        try
        {
            await BeginCut();
        }
        finally
        {
            try
            {
                if (_listener is not null)
                {
                    await _listener.DisposeAsync();
                }
            }
            finally
            {
                try
                {
                    _channel.Dispose();
                }
                finally
                {
                    _generation.Dispose();
                }
            }
        }
    }
}
