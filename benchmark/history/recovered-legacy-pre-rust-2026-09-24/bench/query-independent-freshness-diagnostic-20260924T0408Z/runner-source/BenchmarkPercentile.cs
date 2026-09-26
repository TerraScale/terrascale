namespace TerraBase.Benchmarks;

/// <summary>Nearest-rank percentile helpers used by benchmark summaries.</summary>
internal static class BenchmarkPercentile
{
    public static int NearestRankIndex(int sampleCount, double percentile)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        if (!double.IsFinite(percentile) || percentile is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be finite and between 0 and 1.");
        }

        if (percentile == 0)
        {
            return 0;
        }

        return Math.Min(sampleCount - 1, (int)Math.Ceiling(sampleCount * percentile) - 1);
    }

    public static bool SelfCheck()
    {
        var ordered = Enumerable.Range(1, 30).ToArray();
        return ordered[NearestRankIndex(ordered.Length, 0.50)] == 15
            && ordered[NearestRankIndex(ordered.Length, 0.95)] == 29
            && ordered[NearestRankIndex(ordered.Length, 0.99)] == 30
            && NearestRankIndex(1, 0.95) == 0
            && ordered[NearestRankIndex(ordered.Length, 0)] == 1
            && ordered[NearestRankIndex(ordered.Length, 1)] == 30;
    }
}
