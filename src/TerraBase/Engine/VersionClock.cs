namespace TerraBase.Engine;

/// <summary>
/// The limit of local commit offsets, and of the versions the system database replicates to its catalog followers (TS-647
/// step 5 replaces those with registers).
/// </summary>
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
