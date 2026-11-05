using System.Collections.Concurrent;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Features.HomeApply;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.HomePush;

internal sealed class LocalOverlay
{
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), Pending> _databases = new();

    public event Action<TenantContext, ulong>? Held;

    public event Action<TenantContext, HomeApplyResult>? Reverted;

    public int Count => _databases.Values.Sum(pending => pending.Count);

    public Task<HomeApplyResult> Add(TenantContext database, ulong sequence, RegionTransaction transaction)
    {
        var entry = new Entry(transaction) { Sequence = sequence };
        _databases.GetOrAdd(Key(database), _ => new Pending()).Add(entry);
        return entry.Home.Task;
    }

    public void Applied(TenantContext database, HomeApplyResult result)
    {
        if (_databases.TryGetValue(Key(database), out var pending) && pending.Resolve(result) && result.State == WriteStates.Reverted)
        {
            Reverted?.Invoke(database, result);
        }
    }

    public void Published(TenantContext database, string id, ulong sequence)
    {
        if (_databases.TryGetValue(Key(database), out var pending))
        {
            pending.Published(id, sequence);
        }
    }

    public void Drop(TenantContext database, string id)
    {
        if (_databases.TryGetValue(Key(database), out var pending))
        {
            pending.Drop(id);
        }
    }

    internal void DeleteDatabase(TenantContext database)
    {
        if (_databases.TryRemove(Key(database), out var pending))
        {
            foreach (var entry in pending.Snapshot())
            {
                entry.Home.TrySetCanceled();
            }
        }
    }

    public RegionTransaction? Find(TenantContext database, string id) =>
        _databases.TryGetValue(Key(database), out var pending) ? pending.Find(id) : null;

    public async ValueTask<bool> RestoreAsync(TenantContext database, DatabaseRevision? pulled, IReplicaWriter writer, CancellationToken cancellationToken)
    {
        if (!_databases.TryGetValue(Key(database), out var pending) || pending.Snapshot() is not { Count: > 0 } entries)
        {
            return false;
        }

        var held = await HeldAsync(writer, entries, cancellationToken).ConfigureAwait(false);
        ulong through = 0;
        var restored = false;
        foreach (var entry in entries)
        {
            if (held.TryGetValue(entry.Transaction.Id!, out var result))
            {
                through = Math.Max(through, entry.Sequence);
                pending.Remove(entry, result);
                continue;
            }

            if (entry.Result?.State == WriteStates.Reverted)
            {
                through = Math.Max(through, entry.Sequence);
                pending.Remove(entry, entry.Result);
                continue;
            }

            await PushChanges.ApplyAsync(writer, entry.Transaction.Changes!, cancellationToken).ConfigureAwait(false);
            restored = true;
        }

        if (through > 0 && pending.Snapshot().All(entry => entry.Sequence > through))
        {
            Held?.Invoke(database, through);
        }

        return restored;
    }

    private static async ValueTask<Dictionary<string, HomeApplyResult>> HeldAsync(IReplicaWriter writer, IReadOnlyList<Entry> entries, CancellationToken cancellationToken)
    {
        var held = new Dictionary<string, HomeApplyResult>(StringComparer.Ordinal);
        var table = await writer.ExecuteAsync(ApplySql.Statement("SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = '_ts_applied'"), cancellationToken)
            .ConfigureAwait(false);
        if (table.Rows is not [[long and > 0]])
        {
            return held;
        }

        foreach (var chunk in entries.Chunk(256))
        {
            var found = await writer.ExecuteAsync(ApplySql.Statement($"SELECT id, result FROM _ts_applied WHERE id IN ({string.Join(", ", chunk.Select(_ => "?"))})",
                [.. chunk.Select(entry => (object?)entry.Transaction.Id)]), cancellationToken).ConfigureAwait(false);
            foreach (var row in found.Rows)
            {
                if (row is [string id, string result] && JsonSerializer.Deserialize(result, TerraBaseJsonContext.Default.HomeApplyResult) is { } recorded)
                {
                    held[id] = recorded;
                }
            }
        }

        return held;
    }

    private static (string, string, string) Key(TenantContext database) => (database.OrganizationId, database.ProjectId!, database.DatabaseId!);

    private sealed record Entry(RegionTransaction Transaction)
    {
        public ulong Sequence { get; set; }

        public TaskCompletionSource<HomeApplyResult> Home { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HomeApplyResult? Result { get; set; }
    }

    private sealed class Pending
    {
        private readonly Lock _lock = new();
        private readonly List<Entry> _entries = [];

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _entries.Count(entry => entry.Result is null);
                }
            }
        }

        public void Add(Entry entry)
        {
            lock (_lock)
            {
                _entries.Add(entry);
            }
        }

        public IReadOnlyList<Entry> Snapshot()
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }

        internal void DeleteDatabase(TenantContext database)
    {
        if (_databases.TryRemove(Key(database), out var pending))
        {
            foreach (var entry in pending.Snapshot())
            {
                entry.Home.TrySetCanceled();
            }
        }
    }

    public RegionTransaction? Find(string id)
        {
            lock (_lock)
            {
                return _entries.Find(entry => entry.Transaction.Id == id)?.Transaction;
            }
        }

        public void Published(string id, ulong sequence)
        {
            lock (_lock)
            {
                if (_entries.Find(entry => entry.Transaction.Id == id) is { } entry)
                {
                    entry.Sequence = sequence;
                }
            }
        }

        public void Drop(string id)
        {
            lock (_lock)
            {
                _ = _entries.RemoveAll(entry => entry.Transaction.Id == id);
            }
        }

        public bool Resolve(HomeApplyResult result)
        {
            Entry? entry;
            lock (_lock)
            {
                entry = _entries.Find(candidate => candidate.Transaction.Id == result.Id);
                if (entry is null || entry.Result is not null)
                {
                    return false;
                }

                entry.Result = result;
            }

            entry.Home.TrySetResult(result);
            return true;
        }

        public void Remove(Entry entry, HomeApplyResult result)
        {
            lock (_lock)
            {
                _ = _entries.Remove(entry);
            }

            entry.Home.TrySetResult(result);
        }
    }
}
