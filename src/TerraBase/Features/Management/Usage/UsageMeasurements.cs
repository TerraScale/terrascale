using TerraBase.Features.Stats;

namespace TerraBase.Features.Management.Usage;

internal sealed record UsageDimension(string Meter, string Region, string Kind)
{
    public string Key => $"{Meter}|{Region}|{Kind}";
}

internal sealed record UsageReading(UsageDimension Dimension, long Count, long Value = 0, long[]? Latency = null)
{
    public static IReadOnlyList<double> LatencyBounds => StatsLatency.BoundsMs;

    public bool IsZero => Count == 0 && Value == 0 && (Latency is null || Latency.All(count => count == 0));

    public UsageReading Add(UsageReading other, int sign)
    {
        var count = Merge(Count, other.Count, sign);
        var value = Merge(Value, other.Value, sign);
        var latency = Latency is null && other.Latency is null ? null
            : Enumerable.Range(0, LatencyBounds.Count).Select(i => Merge(Latency?[i] ?? 0, other.Latency?[i] ?? 0, sign)).ToArray();
        return new(Dimension, count, value, latency);
    }

    private static long Merge(long left, long right, int sign) =>
        sign > 0 ? (right > long.MaxValue - left ? long.MaxValue : left + right) : Math.Max(0, left - right);

    public static IReadOnlyDictionary<string, UsageReading>? Combine(
        IReadOnlyDictionary<string, UsageReading>? left, IReadOnlyDictionary<string, UsageReading>? right, int sign)
    {
        if (right is null)
        {
            return left;
        }

        var result = left is null ? new Dictionary<string, UsageReading>(StringComparer.Ordinal) : new(left, StringComparer.Ordinal);
        foreach (var (key, reading) in right)
        {
            var combined = result.TryGetValue(key, out var saved) ? saved.Add(reading, sign) : reading;
            if (combined.IsZero)
            {
                result.Remove(key);
            }
            else
            {
                result[key] = combined;
            }
        }

        return result.Count == 0 ? null : result;
    }
}
