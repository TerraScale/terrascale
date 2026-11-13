using ZLogger;

namespace TerraBase.Features.Backup;

internal sealed class KeyInventory(ILogger logger, HashSet<string> logged)
{
    internal Dictionary<string, long> Counts { get; } = new(StringComparer.Ordinal);

    internal long UnreadableCount { get; private set; }

    internal void Count(string id) => Counts[id] = Counts.GetValueOrDefault(id) + 1;

    internal void Unreadable(string item)
    {
        UnreadableCount++;
        if (logged.Count < 65536 && logged.Add(item))
        {
            logger.ZLogWarning($"Wrapped key inventory cannot read item {item:@Item}; retirement is blocked");
        }
    }
}
