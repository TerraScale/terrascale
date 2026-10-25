namespace TerraBase.Engine;

/// <summary>
/// What kind of storage work the calling flow does. Bulk work (backups, tenant anti-entropy, snapshot exports) reads whole
/// multi-GB files: on a slow disk a couple of its calls held every I/O thread, and a catalog anchor's one-row read missed
/// its 10 s deadline (batch 8, 2026-10-25). The engine runs bulk calls on all but one of its I/O threads.
/// </summary>
public static class StorageWork
{
    private static readonly AsyncLocal<bool> BulkFlow = new();

    /// <summary>Whether the calling flow is bulk work.</summary>
    internal static bool IsBulk => BulkFlow.Value;

    /// <summary>
    /// Marks the calling flow as bulk work until disposed. Only for flows that hold no database writer while they read:
    /// a writer held while a bulk call waits for its share of threads would stall that database's writes.
    /// </summary>
    public static IDisposable Bulk()
    {
        var previous = BulkFlow.Value;
        BulkFlow.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => BulkFlow.Value = previous;
    }
}
