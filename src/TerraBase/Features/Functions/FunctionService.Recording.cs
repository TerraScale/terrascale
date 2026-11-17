using System.Threading.Channels;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Memory;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;

namespace TerraBase.Features.Functions;

internal sealed partial class FunctionService
{
    private const int RecordCapacity = 64;
    private const int RecordBatchSize = 32;
    private const int TenantRecordCapacity = RecordCapacity / 2;
    private readonly Channel<PendingRecord> _records = Channel.CreateBounded<PendingRecord>(new BoundedChannelOptions(RecordCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly CancellationTokenSource _invocationsStopping = new();
    private readonly Dictionary<string, TenantRecords> _tenantRecords = new(StringComparer.Ordinal);
    private int _recordCount;
    private readonly Lock _recordGate = new();
    private Task<Unavailable?> _lastRecord = Task.FromResult<Unavailable?>(null);
    private readonly HashSet<Task<Unavailable?>> _pendingRecords = [];
    private int _running;
    private bool _stopping;

    private sealed class TenantRecords
    {
        public int Count { get; set; }
        public TaskCompletionSource Space { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record PendingRecord(TenantContext Tenant, string Name, FunctionLogBody Log, IMemoryReservation Memory, RegionalIngress.Lease Ingress)
    {
        public TaskCompletionSource<Unavailable?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private Task<Unavailable?> Queue(TenantContext tenant, string name, FunctionLogBody log, IMemoryReservation reservation, RegionalIngress.Lease lease, long retainedBytes)
    {
        var pending = new PendingRecord(tenant, name, log, reservation, lease);
        lock (_recordGate)
        {
            var held = _tenantRecords[tenant.OrganizationId];
            if (held.Count < TenantRecordCapacity)
            {
                memory.Shrink(reservation, Math.Max(0, reservation.Bytes - retainedBytes));
            }
            if (!_records.Writer.TryWrite(pending))
            {
                throw new InvalidOperationException("function recording has stopped");
            }
            _lastRecord = pending.Completion.Task;
            _pendingRecords.Add(_lastRecord);
            return held.Count == TenantRecordCapacity ? WaitForSpaceAsync(held.Space.Task, pending) : Task.FromResult<Unavailable?>(null);
        }
    }

    private static async Task<Unavailable?> WaitForSpaceAsync(Task space, PendingRecord pending)
    {
        await space.ConfigureAwait(false);
        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private async Task<Unavailable?> FlushAsync(CancellationToken cancellationToken)
    {
        Task<Unavailable?>[] pending;
        lock (_recordGate)
        {
            pending = _pendingRecords.Count == 0 ? [_lastRecord] : [.. _pendingRecords];
        }
        var results = await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        return results.FirstOrDefault(error => error is not null);
    }

    private void ReleaseRecord(TenantContext tenant)
    {
        lock (_recordGate)
        {
            _recordCount--;
            var held = _tenantRecords[tenant.OrganizationId];
            if (held.Count-- == TenantRecordCapacity)
            {
                held.Space.SetResult();
            }
            if (held.Count == 0)
            {
                _tenantRecords.Remove(tenant.OrganizationId);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<PendingRecord>(RecordBatchSize);
        while (await _records.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            while (batch.Count < RecordBatchSize && _records.Reader.TryRead(out var pending))
            {
                batch.Add(pending);
            }
            Unavailable? unavailable;
            Exception? failure = null;
            try
            {
                unavailable = await RecordBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                unavailable = new Unavailable("function invocation recording failed", "function_recording_failed");
                failure = error;
            }
            if (unavailable is not null)
            {
                LogRecordingFailed(logger, batch.Count, unavailable.Code, failure);
            }
            foreach (var pending in batch)
            {
                pending.Memory.Dispose();
                pending.Ingress.Dispose();
                lock (_recordGate)
                {
                    pending.Completion.SetResult(unavailable);
                    _pendingRecords.Remove(pending.Completion.Task);
                }
                ReleaseRecord(pending.Tenant);
            }
            batch.Clear();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Function recording failed for {Count} records: {Code}")]
    private static partial void LogRecordingFailed(ILogger logger, int count, string code, Exception? exception);

    private async Task<Unavailable?> RecordBatchAsync(IReadOnlyList<PendingRecord> batch, CancellationToken cancellationToken)
    {
        return await catalog.WriteAsync<Unavailable?>(async transaction =>
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
            return null;
        }, static error => error, cancellationToken).ConfigureAwait(false);
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
        await _invocationsStopping.CancelAsync().ConfigureAwait(false);
        if (ExecuteTask is { } recording)
        {
            await recording.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Dispose()
    {
        _invocationsStopping.Dispose();
        base.Dispose();
    }
}
