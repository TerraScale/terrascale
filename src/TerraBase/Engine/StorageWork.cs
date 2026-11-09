namespace TerraBase.Engine;

/// <summary>
/// What kind of storage work the calling flow does. Bulk work (backups, tenant anti-entropy, snapshot exports) reads whole
/// multi-GB files: on a slow disk a couple of its calls held every I/O thread, and a catalog anchor's one-row read missed
/// its 10 s deadline (batch 8, 2026-10-25). The engine runs bulk calls on all but one of its I/O threads.
/// </summary>
public static class StorageWork
{
    private static readonly AsyncLocal<bool> BulkFlow = new();

    internal static bool IsBulk => BulkFlow.Value;

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
