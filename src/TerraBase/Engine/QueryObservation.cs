namespace TerraBase.Engine;

/// <summary>
/// Candidate document visits made by one query execution, including sort admission and count overflow walks.
/// Repeated visits count separately; fetching an admitted result's body does not count again.
/// </summary>
public sealed class QueryObservation
{
    private long _rowsScanned;

    /// <summary>Actual visits accumulated so far, including work before cancellation or early stream disposal.</summary>
    public long RowsScanned => Interlocked.Read(ref _rowsScanned);

    internal void AddRow() => Interlocked.Increment(ref _rowsScanned);
}
