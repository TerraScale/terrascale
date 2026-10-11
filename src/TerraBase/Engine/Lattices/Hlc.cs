namespace TerraBase.Engine.Lattices;

/// <summary>
/// A hybrid logical clock stamp (Kulkarni et al.): Unix milliseconds in the high 48 bits and a logical counter in the
/// low 16, so stamps order like integers. A stamp never runs behind the physical clock or behind any stamp it saw.
/// </summary>
/// <param name="Value">The packed stamp.</param>
public readonly record struct Hlc(ulong Value)
{
    private const int LogicalBits = 16;

    /// <summary>The physical part, in Unix milliseconds.</summary>
    public long Milliseconds => (long)(Value >> LogicalBits);

    /// <summary>The logical part, which orders stamps within one millisecond.</summary>
    public ushort Logical => (ushort)Value;

    /// <summary>The stamp of a local event (a write, or a send), at physical time <paramref name="nowMilliseconds"/>.</summary>
    public Hlc Tick(long nowMilliseconds) => Next(Value, nowMilliseconds);

    /// <summary>The stamp of receiving <paramref name="remote"/> at physical time <paramref name="nowMilliseconds"/>.</summary>
    public Hlc Observe(Hlc remote, long nowMilliseconds) => Next(Math.Max(Value, remote.Value), nowMilliseconds);

    private static Hlc Next(ulong last, long nowMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nowMilliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(nowMilliseconds, 1L << (64 - LogicalBits));
        var physical = (ulong)nowMilliseconds << LogicalBits;

        // Past the physical clock, the logical counter carries into the milliseconds: still strictly increasing.
        return new(physical > last ? physical : checked(last + 1));
    }
}
