namespace TerraScale.DotNet;

/// <summary>
/// Per-region round-trip tracker (ADR 0050 §2): a time-decayed exponentially
/// weighted moving average with a 60 s halflife, so one slow outlier cannot
/// permanently demote a healthy region. Samples are bounded 0..60000 ms and
/// the tracker is thread-safe.
/// </summary>
internal sealed class TerraScaleRegionRttTracker
{
    internal const int HalflifeMs = 60_000;
    internal const int MaxSampleMs = 60_000;
    internal const int MaxHeaderEntries = 20;

    private sealed record Sample(double ValueMs, long AtMs);

    private readonly object gate = new();
    private readonly Dictionary<string, Sample> samples = new(StringComparer.Ordinal);

    internal Func<long> NowMs { get; set; } = static () => Environment.TickCount64;

    /// <summary>Folds one measured round trip into the region's average.</summary>
    internal void Observe(string region, double rttMs)
    {
        if (!TerraScaleRegionCodes.IsKnown(region) ||
            !double.IsFinite(rttMs) ||
            rttMs is < 0 or > MaxSampleMs)
        {
            return;
        }

        var now = NowMs();
        lock (gate)
        {
            if (samples.TryGetValue(region, out var previous) && previous.AtMs <= now)
            {
                var decay = Math.Pow(2, -(double)(now - previous.AtMs) / HalflifeMs);
                samples[region] = new Sample(previous.ValueMs * decay + rttMs * (1 - decay), now);
            }
            else
            {
                // First sample, or the clock moved backwards: replace.
                samples[region] = new Sample(rttMs, now);
            }
        }
    }

    /// <summary>
    /// The measured averages as one <c>X-TerraScale-Region-Rtt</c> header value
    /// (<c>REGION=ms</c> pairs, at most 20, rounded to whole milliseconds), or
    /// null when no measurable sample exists.
    /// </summary>
    internal string? MeasuredRttHeader()
    {
        List<KeyValuePair<string, Sample>> snapshot;
        lock (gate)
        {
            if (samples.Count == 0)
            {
                return null;
            }

            snapshot = [.. samples];
        }

        var parts = new List<string>();
        foreach (var (region, sample) in snapshot)
        {
            if (sample.ValueMs is < 1 or > MaxSampleMs)
            {
                continue;
            }

            if (parts.Count >= MaxHeaderEntries)
            {
                break;
            }

            parts.Add($"{region}={Math.Round(sample.ValueMs, MidpointRounding.AwayFromZero)}");
        }

        return parts.Count == 0 ? null : string.Join(",", parts);
    }
}
