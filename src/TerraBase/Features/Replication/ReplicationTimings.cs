using System.Diagnostics.Metrics;
using TerraBase.Hosting;

namespace TerraBase.Features.Replication;

/// <summary>
/// How long each step between a local commit and its remote durable ack takes, by step: the sender's ship pass, change log
/// read and ack handling, and the receiver's loop (a frame's arrival until the stream's loop takes it), wait, catalog open,
/// writer wait, join, commit (sync), stable read and the whole batch from arrival to its ack written. The step names are
/// this class's constants, so the label set stays bounded.
/// </summary>
internal sealed class ReplicationTimings(IMeterFactory meters, TimeProvider time)
{
    public const string SendPass = "send.pass";
    public const string SendRead = "send.read";
    public const string SendAck = "send.ack";
    public const string ReceiveLoop = "receive.loop";
    public const string ReceiveWait = "receive.wait";
    public const string ReceiveOpen = "receive.open";
    public const string ReceiveBegin = "receive.begin";
    public const string ReceiveJoin = "receive.join";
    public const string ReceiveCommit = "receive.commit";
    public const string ReceiveStable = "receive.stable";
    public const string ReceiveTotal = "receive.total";

    internal static readonly double[] Buckets = [0.0005, 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.15, 0.2, 0.25, 0.3, 0.5, 1, 2.5, 5, 10];

    private readonly Histogram<double> _steps = meters.Create(Telemetry.MeterName).CreateHistogram<double>("terrabase.replication.step_duration", "s",
        "Duration of one step between a commit and its remote durable ack, by step.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = Buckets });

    public long Now => time.GetTimestamp();

    public TimeSpan Elapsed(long since) => time.GetElapsedTime(since);

    /// <summary>Records <paramref name="step"/> from <paramref name="since"/> (a <see cref="Now"/> value) and returns now.</summary>
    public long Record(string step, long since)
    {
        var now = time.GetTimestamp();
        _steps.Record(time.GetElapsedTime(since, now).TotalSeconds, new KeyValuePair<string, object?>("step", step));
        return now;
    }
}
