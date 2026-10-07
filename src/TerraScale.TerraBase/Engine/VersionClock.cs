namespace TerraBase.Engine;

/// <summary>The shared admission limits for local offsets and imported document versions.</summary>
public static class VersionClock
{
    /// <summary>Largest clock advance one imported transaction may make from its starting head.</summary>
    public const long MaxAdvance = 1L << 32;

    /// <summary>Last allocatable local offset and largest accepted imported version.</summary>
    public const long MaxVersion = long.MaxValue - MaxAdvance;

    /// <summary>Rejects an invalid version or an import requiring explicit recovery of a larger clock gap.</summary>
    public static void ValidateImport(long version, long startingHead)
    {
        if (version <= 0 || version > MaxVersion || (version > startingHead && version - startingHead > MaxAdvance))
        {
            throw new InvalidDataException("a replicated version exceeds the database clock admission limits");
        }
    }

    /// <summary>Refuses local allocation at terminal capacity, including exhausted legacy clocks.</summary>
    public static void EnsureCanAllocate(long head)
    {
        if (head >= MaxVersion)
        {
            throw new OverflowException("the commit offset is exhausted");
        }
    }
}
