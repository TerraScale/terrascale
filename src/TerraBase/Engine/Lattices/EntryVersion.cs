namespace TerraBase.Engine.Lattices;

/// <summary>
/// The version of a document state entry (docs/terrabase/multi-region-replication.md section 3): the stamp of the
/// operation that wrote it and the node that ran it, compared in that order. A node mints each stamp at most once, so
/// two different events never share a version. The default is below every version a node mints.
/// </summary>
/// <param name="Stamp">The operation's stamp.</param>
/// <param name="Node">The node that ran the operation; breaks ties between equal stamps.</param>
public readonly record struct EntryVersion(Hlc Stamp, ReplicaId Node) : IComparable<EntryVersion>
{
    /// <inheritdoc />
    public int CompareTo(EntryVersion other) =>
        Stamp.Value != other.Stamp.Value ? Stamp.Value.CompareTo(other.Stamp.Value) : Node.Value.CompareTo(other.Node.Value);

    /// <summary>Whether <paramref name="left"/> is older than <paramref name="right"/>.</summary>
    public static bool operator <(EntryVersion left, EntryVersion right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>Whether <paramref name="left"/> is newer than <paramref name="right"/>.</summary>
    public static bool operator >(EntryVersion left, EntryVersion right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>Whether <paramref name="left"/> is not newer than <paramref name="right"/>.</summary>
    public static bool operator <=(EntryVersion left, EntryVersion right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>Whether <paramref name="left"/> is not older than <paramref name="right"/>.</summary>
    public static bool operator >=(EntryVersion left, EntryVersion right)
    {
        return left.CompareTo(right) >= 0;
    }
}
