namespace TerraBase.Engine;

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
