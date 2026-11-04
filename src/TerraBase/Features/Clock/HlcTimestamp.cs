using System.Buffers.Binary;
using System.Text;
using TerraBase.Configuration;

namespace TerraBase.Features.Clock;

public readonly record struct HlcTimestamp(long PhysicalMilliseconds, uint Logical, string NodeId) : IComparable<HlcTimestamp>
{
    public const int EncodedLength = 75;

    public void WriteTo(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(PhysicalMilliseconds);
        if (!TerraBaseOptions.IsMachineId(NodeId))
        {
            throw new InvalidOperationException("the HLC must identify a node");
        }

        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException("the HLC destination must contain at least 75 bytes", nameof(destination));
        }

        BinaryPrimitives.WriteInt64BigEndian(destination, PhysicalMilliseconds);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], Logical);
        var node = destination[12..EncodedLength];
        node.Clear();
        _ = Encoding.ASCII.GetBytes(NodeId, node);
    }

    public static HlcTimestamp ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length != EncodedLength)
        {
            throw new InvalidDataException("the HLC encoding must contain exactly 75 bytes");
        }

        var node = source[12..];
        var padding = node.IndexOf((byte)0);
        if (padding >= 0)
        {
            if (node[padding..].IndexOfAnyExcept((byte)0) >= 0)
            {
                throw new InvalidDataException("the HLC node padding is invalid");
            }

            node = node[..padding];
        }

        var physical = BinaryPrimitives.ReadInt64BigEndian(source);
        var nodeId = Encoding.ASCII.GetString(node);
        if (physical < 0 || !TerraBaseOptions.IsMachineId(nodeId))
        {
            throw new InvalidDataException("the encoded HLC is invalid");
        }

        return new HlcTimestamp(physical, BinaryPrimitives.ReadUInt32BigEndian(source[8..]), nodeId);
    }

    public int CompareTo(HlcTimestamp other)
    {
        var physical = PhysicalMilliseconds.CompareTo(other.PhysicalMilliseconds);
        var logical = Logical.CompareTo(other.Logical);
        return physical != 0 ? physical : logical != 0 ? logical : StringComparer.Ordinal.Compare(NodeId, other.NodeId);
    }

    public static bool operator <(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) < 0;
    public static bool operator >(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) > 0;
    public static bool operator <=(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) <= 0;
    public static bool operator >=(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) >= 0;
}

public interface IHlcClock
{
    ValueTask<HlcTimestamp> NextAsync(CancellationToken cancellationToken);
    ValueTask<HlcTimestamp> ObserveAsync(HlcTimestamp remote, CancellationToken cancellationToken);
}

public interface ILocalWriteClock
{
    bool Allowed { get; }
    double SkewMilliseconds { get; }
}

public sealed class ClockSkewException() : InvalidOperationException("clock skew prevents local writes; forward to the database home");
