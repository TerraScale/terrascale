using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Features.Shapes;

internal sealed class ShapeControls(IMemoryBudget memory, IHostApplicationLifetime lifetime) : IDisposable
{
    private const int Capacity = 256;
    private readonly Lock _gate = new();
    private readonly Dictionary<Key, Entry> _entries = [];

    internal async ValueTask<LineageResult> ReadAsync(IDatabaseStore store, TenantContext database, string table, ShapeQuery query,
        Func<CancellationToken, ValueTask<LineageResult>> read, CancellationToken cancellationToken)
    {
        var key = new Key(database, store, store.CommitSequence, table, query);
        Entry entry;
        var start = false;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                if (_entries.Count >= Capacity)
                {
                    var oldest = _entries.FirstOrDefault(pair => pair.Value.Result.Task.IsCompleted);
                    if (oldest.Value is null)
                    {
                        return new QuotaExceeded("shape control reads are at capacity; retry later", "shape_busy");
                    }

                    _entries.Remove(oldest.Key);
                    oldest.Value.Memory.Dispose();
                }

                var bytes = 4096L + 2L * (table.Length + (query.Where?.Length ?? 0) + (query.Columns?.Length ?? 0)
                    + (query.Grant?.Where?.Length ?? 0) + (query.Grant?.Columns?.Length ?? 0));
                if (memory.TryReserve(MemoryLane.Foreground, bytes, database) is not { } reservation)
                {
                    return new QuotaExceeded("shape control reads are at capacity; retry later", "shape_busy");
                }

                entry = new Entry(reservation);
                _entries.Add(key, entry);
                start = true;
            }
        }

        if (start)
        {
            _ = CompleteAsync(key, entry, read);
        }

        return await entry.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteAsync(Key key, Entry entry, Func<CancellationToken, ValueTask<LineageResult>> read)
    {
        try
        {
            using var scope = QosContext.Enter(key.Database, memory);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await read(deadline.Token).ConfigureAwait(false);
            entry.Result.SetResult(result);
            if (result is not ResolvedShape)
            {
                Remove(key, entry);
            }
        }
        catch (Exception error)
        {
            entry.Result.SetException(error);
            _ = entry.Result.Task.Exception;
            Remove(key, entry);
        }
    }

    private void Remove(Key key, Entry entry)
    {
        lock (_gate)
        {
            if (_entries.Remove(key))
            {
                entry.Memory.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Memory.Dispose();
            }

            _entries.Clear();
        }
    }

    private sealed record Key(TenantContext Database, IDatabaseStore Store, long Sequence, string Table, ShapeQuery Query);
    private sealed record Entry(IMemoryReservation Memory)
    {
        internal TaskCompletionSource<LineageResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
