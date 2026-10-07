using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TerraBase.Hosting;

namespace TerraBase.Features.CrossRegion;

/// <summary>Cross-region instruments. Tags are bounded: a configured region and a reason name, never a database or member.</summary>
internal sealed class WanMetrics
{
    private readonly Counter<long> _pulledGroups;
    private readonly Counter<long> _pulledBytes;
    private readonly Counter<long> _servedGroups;
    private readonly Counter<long> _failures;
    private readonly ConcurrentDictionary<string, Round> _rounds = new(StringComparer.Ordinal);

    public WanMetrics(IMeterFactory meters, TimeProvider time)
    {
        var meter = meters.Create(Telemetry.MeterName);
        var started = time.GetTimestamp();
        _pulledGroups = meter.CreateCounter<long>("terrabase.wan.pulled.groups", "{group}", "Change groups pulled from another region and applied, by region.");
        _pulledBytes = meter.CreateCounter<long>("terrabase.wan.pulled.encoded", "By", "Envelope bytes pulled from another region, after LZ4, by region.");
        _servedGroups = meter.CreateCounter<long>("terrabase.wan.served.groups", "{group}", "Change groups this member served to other regions.");
        _failures = meter.CreateCounter<long>(
            "terrabase.wan.failures", "{failure}",
            "Cross-region failures, by region and reason: heads (no member answered), pull (a request failed), apply (a group was refused), ahead (an origin went back in time).");
        meter.CreateObservableGauge("terrabase.wan.lag", () => Measure(round => round.Lag), "{group}",
            "Change groups the members of another region that answered this member's last round committed and this member has not applied, by region.");
        meter.CreateObservableGauge("terrabase.wan.staleness", () => Measure(round => time.GetElapsedTime(round.CaughtUp ?? started).TotalSeconds), "s",
            "Seconds since a round of this member reached every member another region lists and held all their change groups, by region.");
        meter.CreateObservableGauge("terrabase.wan.members", () => Measure(round => round.Members), "{member}",
            "Members of another region that answered this member's last round, by region.");
        meter.CreateObservableGauge("terrabase.wan.unregistered", () => Measure(round => round.Unregistered), "{database}",
            "Databases another region holds that do not replicate here, by region: their project is not registered here, or they were deleted here.");
        meter.CreateObservableGauge("terrabase.wan.conflicts", () => Measure(round => round.Conflicts), "{database}",
            "Databases another region holds that do not replicate here because another database of their project has their name here, by region.");

        IEnumerable<Measurement<T>> Measure<T>(Func<Round, T> value) where T : struct
        {
            return _rounds.Select(round => new Measurement<T>(value(round.Value), new KeyValuePair<string, object?>("region", round.Key)));
        }
    }

    /// <summary>
    /// A region's round ended: <paramref name="lag"/> groups behind the members that answered; a round that held every group
    /// of every member the region lists (<paramref name="caughtUp"/>) records the time.
    /// </summary>
    public void Rounded(string region, long lag, int members, int unregistered, int conflicts, bool caughtUp, long now) =>
        _rounds.AddOrUpdate(
            region,
            _ => new Round(lag, members, unregistered, conflicts, caughtUp ? now : null),
            (_, last) => new Round(lag, members, unregistered, conflicts, caughtUp ? now : last.CaughtUp));

    /// <summary>No member of the region answered the heads: its lag is unknown, and staleness keeps growing.</summary>
    public void Unanswered(string region) =>
        _rounds.AddOrUpdate(region, _ => new Round(0, 0, 0, 0, null), (_, last) => last with { Members = 0 });

    public void Pulled(string region, uint encodedBytes)
    {
        var tag = new KeyValuePair<string, object?>("region", region);
        _pulledGroups.Add(1, tag);
        _pulledBytes.Add(encodedBytes, tag);
    }

    public void Served(int groups) => _servedGroups.Add(groups);

    public void Failed(string region, string reason) =>
        _failures.Add(1, new KeyValuePair<string, object?>("region", region), new KeyValuePair<string, object?>("reason", reason));

    private sealed record Round(long Lag, int Members, int Unregistered, int Conflicts, long? CaughtUp);
}
