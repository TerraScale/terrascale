namespace TerraBase.Engine;

public static class VersionClock
{
    public const long MaxVersion = long.MaxValue - (1L << 32);

    public static void EnsureCanAllocate(long head)
    {
        if (head >= MaxVersion)
        {
            throw new OverflowException("the commit offset is exhausted");
        }
    }
}
