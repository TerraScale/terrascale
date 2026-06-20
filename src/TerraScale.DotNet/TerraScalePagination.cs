namespace TerraScale.DotNet;

internal static class TerraScalePagination
{
    public static void Validate(int skip, int take)
    {
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), "Skip must be greater than or equal to zero.");
        }

        if (take <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "Take must be greater than zero.");
        }
    }

    public static void ValidateOptional(int? skip, int? take)
    {
        if (skip is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), "Skip must be greater than or equal to zero.");
        }

        if (take is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "Take must be greater than zero.");
        }
    }
}
