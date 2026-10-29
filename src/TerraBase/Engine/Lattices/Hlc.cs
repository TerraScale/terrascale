namespace TerraBase.Engine.Lattices;

public readonly record struct Hlc(ulong Value)
{
    private const int LogicalBits = 16;

    public long Milliseconds => (long)(Value >> LogicalBits);

    public ushort Logical => (ushort)Value;

    public Hlc Tick(long nowMilliseconds) => Next(Value, nowMilliseconds);

    private static Hlc Next(ulong last, long nowMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nowMilliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(nowMilliseconds, 1L << (64 - LogicalBits));
        var physical = (ulong)nowMilliseconds << LogicalBits;

        return new(physical > last ? physical : checked(last + 1));
    }
}
