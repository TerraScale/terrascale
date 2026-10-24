namespace TerraBase.Engine.Turso;

/// <summary>
/// The sign codes of recently queried vector indexes, so a near query does not read every code row through Turso's small
/// per-connection page cache (docs/terrabase/vector-index.md). An entry is valid for one generation of its index: every
/// change to the codes writes a new random generation in the same transaction, and only read transactions, which see
/// committed state, fill or use entries. Bounded by entries and bytes; the least recently used goes first.
/// </summary>
internal static class VectorCodeCache
{
    private const int MaxEntries = 64;
    private const long MaxBytes = 128L * 1024 * 1024;

    private static readonly Lock Gate = new();
    private static readonly LinkedList<Entry> Entries = [];
    private static long _bytes;

    /// <summary>One index's occupied slots and their codes, <see cref="Words"/> 64-bit words each, in slot order.</summary>
    internal sealed record Codes(long Generation, int Words, long[] Slots, ulong[] Values)
    {
        public long Bytes => (8L * Slots.Length) + (8L * Values.Length) + 64;
    }

    private sealed record Entry(WeakReference<TursoDatabaseStore> Store, string Index, Codes Codes);

    public static Codes? Get(TursoDatabaseStore store, string index, long generation)
    {
        lock (Gate)
        {
            for (var node = Entries.First; node is not null; node = node.Next)
            {
                if (node.Value.Index == index && node.Value.Store.TryGetTarget(out var target) && ReferenceEquals(target, store))
                {
                    if (node.Value.Codes.Generation != generation)
                    {
                        return null;
                    }

                    Entries.Remove(node);
                    Entries.AddFirst(node);
                    return node.Value.Codes;
                }
            }

            return null;
        }
    }

    public static void Put(TursoDatabaseStore store, string index, Codes codes)
    {
        if (codes.Bytes > MaxBytes)
        {
            return;
        }

        lock (Gate)
        {
            for (var node = Entries.First; node is not null;)
            {
                var next = node.Next;
                // Replaced, or its store is gone.
                if ((node.Value.Index == index && node.Value.Store.TryGetTarget(out var target) && ReferenceEquals(target, store))
                    || !node.Value.Store.TryGetTarget(out _))
                {
                    Remove(node);
                }

                node = next;
            }

            while (Entries.Count >= MaxEntries || _bytes + codes.Bytes > MaxBytes)
            {
                Remove(Entries.Last!);
            }

            Entries.AddFirst(new Entry(new WeakReference<TursoDatabaseStore>(store), index, codes));
            _bytes += codes.Bytes;
        }
    }

    private static void Remove(LinkedListNode<Entry> node)
    {
        Entries.Remove(node);
        _bytes -= node.Value.Codes.Bytes;
    }
}
