namespace TerraBase.Engine.Lattices;

// The entries of a DocumentState (docs/terrabase/multi-region-replication.md section 4). Each one carries Changed: the
// stamp of the last local operation that wrote it, so the tailer can rebuild that operation's delta. Changed is
// bookkeeping: equality, joins and digests ignore it.

/// <summary>The clear register of a document: PUT writes it live, DELETE writes it deleted.</summary>
/// <param name="Version">The write that set it.</param>
/// <param name="Deleted">Whether that write deleted the document.</param>
/// <param name="Changed">Bookkeeping: the stamp of the operation that last wrote this entry.</param>
public readonly record struct ClearEntry(EntryVersion Version, bool Deleted, Hlc Changed)
{
    /// <inheritdoc />
    public bool Equals(ClearEntry other) => Version == other.Version && Deleted == other.Deleted;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Version, Deleted);

    // The larger version wins; a deleted clear wins a tie, which only a node that minted one version twice can cause.
    internal static ClearEntry? Join(ClearEntry? left, ClearEntry? right) =>
        left is not { } l ? right
        : right is not { } r ? left
        : (l.Version, l.Deleted).CompareTo((r.Version, r.Deleted)) >= 0 ? l : r;
}

/// <summary>The last-writer-wins base of a field: a value, or a tombstone. Its version is the field's epoch.</summary>
/// <param name="Version">The write that set it.</param>
/// <param name="Value">The value as compact JSON, or null for a tombstone.</param>
/// <param name="Changed">Bookkeeping: the stamp of the operation that last wrote this entry.</param>
public readonly record struct BaseEntry(EntryVersion Version, string? Value, Hlc Changed)
{
    /// <inheritdoc />
    public bool Equals(BaseEntry other) => Version == other.Version && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Version, Value);

    // The larger version wins; a tie between different values (one version minted twice) still picks one deterministically.
    internal static BaseEntry? Join(BaseEntry? left, BaseEntry? right)
    {
        if (left is not { } l)
        {
            return right;
        }

        if (right is not { } r)
        {
            return left;
        }

        var order = l.Version.CompareTo(r.Version);
        if (order == 0)
        {
            order = l.Value is null ? (r.Value is null ? 0 : -1) : r.Value is null ? 1 : Utf8Ordinal.Instance.Compare(l.Value, r.Value);
        }

        return order >= 0 ? l : r;
    }
}

/// <summary>One node's part of a PN-counter field, valid for one epoch of the field.</summary>
/// <param name="Epoch">The field's epoch when the node counted.</param>
/// <param name="Increments">The sum of the node's positive increments in that epoch.</param>
/// <param name="Decrements">The sum of the node's negative increments in that epoch, as a positive number.</param>
/// <param name="Changed">Bookkeeping: the stamp of the operation that last wrote this entry.</param>
public readonly record struct CounterEntry(EntryVersion Epoch, ulong Increments, ulong Decrements, Hlc Changed)
{
    /// <inheritdoc />
    public bool Equals(CounterEntry other) => Epoch == other.Epoch && Increments == other.Increments && Decrements == other.Decrements;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Epoch, Increments, Decrements);

    // The larger epoch wins; within one epoch both sums only grow, so the join is their pointwise maximum.
    internal static CounterEntry Join(CounterEntry left, CounterEntry right) =>
        left.Epoch != right.Epoch
            ? left.Epoch > right.Epoch ? left : right
            : new(left.Epoch, Math.Max(left.Increments, right.Increments), Math.Max(left.Decrements, right.Decrements),
                new Hlc(Math.Max(left.Changed.Value, right.Changed.Value)));
}

/// <summary>An add of an element to a set field: the element and the version of the add.</summary>
/// <param name="Element">The element as compact JSON.</param>
/// <param name="Version">The add's version. Elements of an array base are implicit adds at the base's version.</param>
public readonly record struct DotKey(string Element, EntryVersion Version);

/// <summary>Whether a <see cref="DotKey"/> was removed.</summary>
/// <param name="RemovedAt">The newest remove that saw the add, or null while the add stands.</param>
/// <param name="Changed">Bookkeeping: the stamp of the operation that last wrote this entry.</param>
public readonly record struct DotEntry(EntryVersion? RemovedAt, Hlc Changed)
{
    /// <inheritdoc />
    public bool Equals(DotEntry other) => RemovedAt == other.RemovedAt;

    /// <inheritdoc />
    public override int GetHashCode() => RemovedAt.GetHashCode();

    // The newer remove wins; any remove beats none.
    internal static DotEntry Join(DotEntry left, DotEntry right) =>
        right.RemovedAt is not { } removed || (left.RemovedAt is { } mine && mine >= removed) ? left : right;
}

/// <summary>
/// Orders strings by their UTF-8 bytes (code points), so names and elements sort the same on every platform. UTF-16
/// ordinal order differs from it only between surrogates and U+E000..U+FFFF.
/// </summary>
internal sealed class Utf8Ordinal : IComparer<string>, IComparer<DotKey>
{
    public static readonly Utf8Ordinal Instance = new();

    public int Compare(string? x, string? y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        var length = Math.Min(x.Length, y.Length);
        for (var i = 0; i < length; i++)
        {
            if (x[i] != y[i])
            {
                return CodePointOrder(x[i]) - CodePointOrder(y[i]);
            }
        }

        return x.Length - y.Length;
    }

    public int Compare(DotKey x, DotKey y)
    {
        var order = Compare(x.Element, y.Element);
        return order != 0 ? order : x.Version.CompareTo(y.Version);
    }

    // Moves surrogates (D800..DFFF) above U+E000..U+FFFF, where their code points are.
    private static int CodePointOrder(char c) => c >= 0xE000 ? c - 0x800 : c >= 0xD800 ? c + 0x2000 : c;
}
