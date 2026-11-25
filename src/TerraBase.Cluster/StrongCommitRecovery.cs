using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TerraBase.Cluster;

public sealed class StrongCommitRecovery(TimeProvider timeProvider, ILogger<StrongCommitRecovery> logger)
{
    private const int Capacity = 256;
    private const int Workers = 8;

    private readonly Channel<Func<Task>> _pending = Channel.CreateBounded<Func<Task>>(Capacity);
    private readonly Lock _gate = new();
    private int _reserved;
    private bool _closing;

    internal bool TryReserve()
    {
        lock (_gate)
        {
            if (_closing || _reserved >= Capacity)
            {
                return false;
            }

            _reserved++;
            return true;
        }
    }

    internal void Enqueue(Func<Task> work)
    {
        if (!_pending.Writer.TryWrite(work))
        {
            throw new InvalidOperationException("strong commit recovery enqueue requires a reserved slot");
        }
    }

    internal void Release()
    {
        lock (_gate)
        {
            _reserved--;
            CompleteIfDrained();
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            _closing = true;
            CompleteIfDrained();
        }
    }

    internal Task RunAsync() => Task.WhenAll(Enumerable.Range(0, Workers).Select(
        _ => ClusterLoop.RunAsync("strong-commit-recovery", ConsumeAsync, timeProvider, logger, CancellationToken.None)));

    private void CompleteIfDrained()
    {
        if (_closing && _reserved == 0)
        {
            _pending.Writer.TryComplete();
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        await foreach (var work in _pending.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await work();
            }
            finally
            {
                Release();
            }
        }
    }
}
