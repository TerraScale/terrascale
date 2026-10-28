namespace TerraBase.Features.Replication;

/// <summary>Limits work under a replica writer, leaving time for admission, observation and the durable commit.</summary>
internal static class ReplicationWriteBatch
{
    internal const int MaxItems = 256;
    internal static readonly TimeSpan MaxDuration = TimeSpan.FromMilliseconds(250);

    // Always admit one indivisible group, even when it exceeds the work limit. Start after acquiring the writer:
    // queueing behind another writer must not spend this transaction's budget before it can do any work.
    internal static bool CanAdd(int items, int next, TimeSpan elapsed) =>
        items == 0 || items + next <= MaxItems && elapsed < MaxDuration;
}
