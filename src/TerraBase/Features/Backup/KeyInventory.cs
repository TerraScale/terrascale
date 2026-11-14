using ZLogger;

namespace TerraBase.Features.Backup;

internal sealed class KeyInventory(ILogger logger, HashSet<string> logged, KeyRewrapFaults faults)
{
    internal Dictionary<string, long> Counts { get; } = new(StringComparer.Ordinal);

    internal long UnreadableCount { get; private set; }

    internal void Count(string id) => Counts[id] = Counts.GetValueOrDefault(id) + 1;

    internal void Unreadable(string item)
    {
        if (faults.Contains(item))
        {
            return;
        }
        UnreadableCount++;
        if (logged.Count < 65536 && logged.Add(item))
        {
            logger.ZLogWarning($"Wrapped key maintenance cannot read item {item:@Item}; the item is retained");
        }
    }
}

internal sealed class KeyRewrapFaults(ILogger<KeyRewrapFaults> logger)
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _items = new(StringComparer.Ordinal);

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    internal bool Contains(string item)
    {
        lock (_gate)
        {
            return _items.Contains(item);
        }
    }

    internal void Failed(string item)
    {
        lock (_gate)
        {
            if (_items.Add(item))
            {
                logger.ZLogWarning($"Wrapped key verification failed for item {item:@Item}; the original wrapper is retained");
            }
        }
    }

    internal void Verified(string item)
    {
        lock (_gate)
        {
            _items.Remove(item);
        }
    }
}
