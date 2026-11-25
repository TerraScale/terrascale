namespace TerraScale.Contracts.Consistency;

public readonly record struct HlcTimestamp(long WallMs, ushort Logical, ushort NodeId) : IComparable<HlcTimestamp>
{
    public const int EncodedSize = 10;

    public const long MaxWallMs = 253402300799999;

    public static HlcTimestamp Zero => default;

    public bool IsValid => WallMs is >= 0 and <= MaxWallMs;

    public DateTimeOffset WallTime => DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(WallMs, 0, MaxWallMs));

    public int CompareTo(HlcTimestamp other)
    {
        var wall = WallMs.CompareTo(other.WallMs);
        if (wall != 0)
        {
            return wall;
        }

        var logical = Logical.CompareTo(other.Logical);
        return logical != 0 ? logical : NodeId.CompareTo(other.NodeId);
    }

    public static bool operator <(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) < 0;

    public static bool operator >(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) > 0;

    public static bool operator <=(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) <= 0;

    public static bool operator >=(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) >= 0;
}
