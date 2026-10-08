namespace TerraBase.Features.Management.Usage;

/// <summary>A usage meter's bounded dimensions. Region is the observing node; PeerRegion names the other end.</summary>
internal sealed record UsageDimension(string Meter, string Region, string PeerRegion, string Kind)
{
    public string Key => $"{Meter}|{Region}|{PeerRegion}|{Kind}";
}

/// <summary>The latest observed replication state; lag is the age of an outstanding backlog, in milliseconds.</summary>
internal sealed record UsageReplicationState(DateTimeOffset ObservedAt, double LagMs, bool Diverged, double? ConvergenceMs, DateTimeOffset? LastApplied, long Remaining = 0);

/// <summary>Additive meter values and mergeable request latency histogram; replication state keeps the newest observation.</summary>
internal sealed record UsageReading(UsageDimension Dimension, long Count, long Value = 0, long[]? Latency = null, UsageReplicationState? State = null)
{
    // Upper bounds in milliseconds. The last bin includes longer requests.
    public static IReadOnlyList<double> LatencyBounds { get; } = [0.25, 0.5, 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536];

    public bool IsZero => Count == 0 && Value == 0 && (Latency is null || Latency.All(count => count == 0));

    public UsageReading Add(UsageReading other, int sign)
    {
        var count = Count + sign * other.Count;
        var value = Value + sign * other.Value;
        var latency = Latency is null && other.Latency is null ? null
            : Enumerable.Range(0, LatencyBounds.Count).Select(i => (Latency?[i] ?? 0) + sign * (other.Latency?[i] ?? 0)).ToArray();
        var state = sign > 0 && other.State is { } newer && (State is null || newer.ObservedAt >= State.ObservedAt)
            ? newer with { ConvergenceMs = newer.ConvergenceMs ?? State?.ConvergenceMs, LastApplied = newer.LastApplied ?? State?.LastApplied } : State;
        return new(Dimension, count, value, latency, count == 0 && value == 0 ? null : state);
    }

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
            else if (result.ContainsKey(key) || result.Count < UsageMeter.MaxDimensions)
            {
                result[key] = combined;
            }
        }

        return result.Count == 0 ? null : result;
    }
}
