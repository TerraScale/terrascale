namespace TerraBase.Engine.Lattices;

public readonly record struct ClearEntry(EntryVersion Version, bool Deleted, Hlc Changed)
{
    public bool Equals(ClearEntry other) => Version == other.Version && Deleted == other.Deleted;

    public override int GetHashCode() => HashCode.Combine(Version, Deleted);

}

public readonly record struct BaseEntry(EntryVersion Version, string? Value, Hlc Changed)
{
    public bool Equals(BaseEntry other) => Version == other.Version && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Version, Value);

}

public readonly record struct CounterEntry(EntryVersion Epoch, ulong Increments, ulong Decrements, Hlc Changed)
{
    public bool Equals(CounterEntry other) => Epoch == other.Epoch && Increments == other.Increments && Decrements == other.Decrements;

    public override int GetHashCode() => HashCode.Combine(Epoch, Increments, Decrements);

}

public readonly record struct DotKey(string Element, EntryVersion Version);

public readonly record struct DotEntry(EntryVersion? RemovedAt, Hlc Changed)
{
    public bool Equals(DotEntry other) => RemovedAt == other.RemovedAt;

    public override int GetHashCode() => RemovedAt.GetHashCode();

}

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

    private static int CodePointOrder(char c) => c >= 0xE000 ? c - 0x800 : c >= 0xD800 ? c + 0x2000 : c;
}
