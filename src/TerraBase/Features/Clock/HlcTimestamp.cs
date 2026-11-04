namespace TerraBase.Features.Clock;

public readonly record struct HlcTimestamp(long PhysicalMilliseconds, uint Logical, string NodeId) : IComparable<HlcTimestamp>
{
    public int CompareTo(HlcTimestamp other)
    {
        var physical = PhysicalMilliseconds.CompareTo(other.PhysicalMilliseconds);
        var logical = Logical.CompareTo(other.Logical);
        return physical != 0 ? physical : logical != 0 ? logical : StringComparer.Ordinal.Compare(NodeId, other.NodeId);
    }
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
