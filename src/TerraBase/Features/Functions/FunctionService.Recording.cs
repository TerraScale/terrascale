using System.Threading.Channels;
using TerraBase.Features.Management;
using TerraBase.Features.Memory;
using TerraBase.Tenancy;

namespace TerraBase.Features.Functions;

internal sealed partial class FunctionService
{
    private const int RecordCapacity = 64;
    private const int RecordBatchSize = 32;
    private readonly Channel<PendingRecord> _records = Channel.CreateBounded<PendingRecord>(new BoundedChannelOptions(RecordCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly SemaphoreSlim _recordSlots = new(RecordCapacity, RecordCapacity);
    private readonly Lock _recordGate = new();
    private Task _lastRecord = Task.CompletedTask;
    private int _running;
    private bool _stopping;

    private sealed record PendingRecord(TenantContext Tenant, string Name, FunctionLogBody Log, IMemoryReservation Memory)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void Queue(TenantContext tenant, string name, FunctionLogBody log, IMemoryReservation reservation)
    {
        var pending = new PendingRecord(tenant, name, log, reservation);
        lock (_recordGate)
        {
            if (!_records.Writer.TryWrite(pending))
            {
                throw new InvalidOperationException("function recording has stopped");
            }
            _lastRecord = pending.Completion.Task;
        }
    }

    private Task FlushAsync(CancellationToken cancellationToken)
    {
        lock (_recordGate)
        {
            return _lastRecord.WaitAsync(cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<PendingRecord>(RecordBatchSize);
        try
        {
            while (await _records.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (batch.Count < RecordBatchSize && _records.Reader.TryRead(out var pending))
                {
                    batch.Add(pending);
                }
                await RecordBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
                foreach (var pending in batch)
                {
                    pending.Memory.Dispose();
                    pending.Completion.SetResult();
                    _recordSlots.Release();
                }
                batch.Clear();
            }
        }
        catch (Exception error)
        {
            _records.Writer.TryComplete(error);
            lock (_recordGate)
            {
                _stopping = true;
            }
            while (_records.Reader.TryRead(out var pending))
            {
                batch.Add(pending);
            }
            foreach (var pending in batch)
            {
                pending.Memory.Dispose();
                pending.Completion.TrySetException(error);
                _recordSlots.Release();
            }
            throw;
        }
    }

    private async Task RecordBatchAsync(IReadOnlyList<PendingRecord> batch, CancellationToken cancellationToken)
    {
        var recorded = await catalog.WriteAsync(async transaction =>
        {
            foreach (var group in batch.GroupBy(pending => (pending.Tenant, pending.Name)))
            {
                var (tenant, name) = group.Key;
                if (await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false) is null)
                {
                    continue;
                }
                var logs = group.Select(pending => pending.Log).ToArray();
                foreach (var log in logs)
                {
                    await SystemCatalog.PutAsync(transaction, Logs(tenant, name), log.Id, log,
                        FunctionsJsonContext.Default.FunctionLogBody, cancellationToken).ConfigureAwait(false);
                }
                await FunctionMetrics.RecordAsync(transaction, tenant, name, logs, cancellationToken).ConfigureAwait(false);
                var kept = await transaction.ListAsync(Logs(tenant, name), null, KeptLogs + logs.Length, 8 * 1024 * 1024, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var log in kept.Records.Take(Math.Max(0, kept.Records.Count - KeptLogs)))
                {
                    await transaction.DeleteAsync(Logs(tenant, name), log.Id, cancellationToken).ConfigureAwait(false);
                }
            }
            return true;
        }, static _ => false, cancellationToken).ConfigureAwait(false);
        if (!recorded)
        {
            throw new InvalidOperationException("function invocation records could not be persisted");
        }
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        lock (_recordGate)
        {
            _stopping = true;
            if (_running == 0)
            {
                _records.Writer.TryComplete();
            }
        }
        if (ExecuteTask is { } recording)
        {
            await recording.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Dispose()
    {
        _recordSlots.Dispose();
        base.Dispose();
    }
}
