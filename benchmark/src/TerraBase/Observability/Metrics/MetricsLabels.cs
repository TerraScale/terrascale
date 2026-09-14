using Ardalis.SmartEnum;

namespace TerraBase;

/// <summary>A closed Region vocabulary used by metrics and routing adapters.</summary>
public enum Region
{
    /// <summary>São Paulo.</summary>
    Gru,

    /// <summary>San Francisco.</summary>
    Sfo,

    /// <summary>Frankfurt.</summary>
    Fra,

    /// <summary>No admitted Region is known.</summary>
    Unknown,
}

/// <summary>Canonical Prometheus labels for admitted TerraBase regions.</summary>
internal sealed class RegionMetricLabel : SmartEnum<RegionMetricLabel>
{
    public static readonly RegionMetricLabel Gru = new(nameof(Gru), Region.Gru, "GRU");
    public static readonly RegionMetricLabel Sfo = new(nameof(Sfo), Region.Sfo, "SFO");
    public static readonly RegionMetricLabel Fra = new(nameof(Fra), Region.Fra, "FRA");
    public static readonly RegionMetricLabel Unknown = new(nameof(Unknown), Region.Unknown, "unknown");

    private RegionMetricLabel(string name, Region region, string label)
        : base(name, (int)region) => Label = label;

    public string Label { get; }

    public static string For(Region region) =>
        TryFromValue((int)region, out var value) ? value.Label : Unknown.Label;
}

/// <summary>A closed operation vocabulary for aggregate request metrics.</summary>
public enum Operation
{
    /// <summary>A point or range read.</summary>
    Read,

    /// <summary>A document mutation.</summary>
    Write,

    /// <summary>A query execution.</summary>
    Query,

    /// <summary>A document deletion.</summary>
    Delete,

    /// <summary>A reactive subscription operation.</summary>
    Subscribe,

    /// <summary>A backup operation.</summary>
    Backup,

    /// <summary>A restore operation.</summary>
    Restore,

    /// <summary>A repair operation.</summary>
    Repair,

    /// <summary>An operation without a more specific family.</summary>
    Other,
}

/// <summary>A closed operation outcome vocabulary.</summary>
public enum Outcome
{
    /// <summary>The operation completed successfully.</summary>
    Success,

    /// <summary>The operation failed.</summary>
    Error,

    /// <summary>Admission or validation refused the operation.</summary>
    Rejected,

    /// <summary>The operation exceeded its deadline.</summary>
    Timeout,

    /// <summary>A version precondition failed.</summary>
    Conflict,

    /// <summary>The requested object was absent.</summary>
    NotFound,
}

/// <summary>An immutable fixed-bucket latency histogram.</summary>
public sealed record HistogramSnapshot(
    IReadOnlyList<long> CumulativeBuckets,
    long Count,
    double SumSeconds);

/// <summary>A bounded request counter and latency series.</summary>
public sealed record RequestSeries(
    Region Region,
    Operation Operation,
    Outcome Outcome,
    long Total,
    HistogramSnapshot Duration);

/// <summary>A bounded in-flight operation gauge.</summary>
public sealed record InFlightSeries(
    Operation Operation,
    long Count);

/// <summary>A read-only source for Prometheus adapters.</summary>
public interface IMetricsSource
{
    /// <summary>Takes one immutable metrics snapshot.</summary>
    MetricsSnapshot Snapshot();

    /// <summary>Renders one bounded exposition body.</summary>
    string MetricsBody();
}
