using System.Diagnostics.Metrics;
using TerraBase.Hosting;

namespace TerraBase.Features.Replication;

/// <summary>Replication instruments. Tags use configured peer positions and 128 reusable database slots, never tenant identities.</summary>
internal sealed class ReplicationMetrics
{
    private readonly Counter<long> _sentGroups;
    private readonly Counter<long> _sentPlainBytes;
    private readonly Counter<long> _sentEncodedBytes;
    private readonly Counter<long> _receivedGroups;
    private readonly Counter<long> _creditWaits;
    private readonly Counter<long> _prefixMismatches;
    private readonly Counter<long> _divergenceDetections;
    private readonly Counter<long> _discoveryPasses;
    private readonly Counter<long> _repairedKeys;
    private readonly Histogram<double> _writeWaits;
    private readonly Histogram<double> _convergence;
    private readonly Meter _meter;

    public ReplicationMetrics(IMeterFactory meters)
    {
        var meter = _meter = meters.Create(Telemetry.MeterName);
        _sentGroups = meter.CreateCounter<long>("terrabase.replication.sent.groups", "{group}", "Change groups sent to peers.");
        _sentPlainBytes = meter.CreateCounter<long>("terrabase.replication.sent.plain", "By", "Change group bytes sent to peers, before LZ4.");
        _sentEncodedBytes = meter.CreateCounter<long>("terrabase.replication.sent.encoded", "By", "Envelope bytes sent to peers, after LZ4.");
        _receivedGroups = meter.CreateCounter<long>(
            "terrabase.replication.received.groups", "{group}", "Change groups received from peers, by result: applied, duplicate or rejected.");
        _prefixMismatches = meter.CreateCounter<long>("terrabase.replication.sender.prefix.mismatches", description: "Peer journal prefixes rejected because their content differs");
        _discoveryPasses = meter.CreateCounter<long>("terrabase.replication.sender.discovery.passes", description: "Completed journal discovery passes, including passes with failures.");
        _divergenceDetections = meter.CreateCounter<long>("terrabase.replication.divergence.detections", description: "Detected journal conflicts across every database, including those omitted from detail.");
        _repairedKeys = meter.CreateCounter<long>("terrabase.replication.repaired.keys", "{key}", "Keys changed by durable anti-entropy merges, excluding duplicates and losing states.");
        _creditWaits = meter.CreateCounter<long>(
            "terrabase.replication.sender.credit.waits", "{wait}", "Prepared groups held until durable acknowledgments release byte credit.");
        _convergence = meter.CreateHistogram<double>(
            "terrabase.replication.convergence", "s", "Observed backlog catch-up time to the latest durable head; database slots are reused after deletion.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.01, 0.1, 0.5, 1, 2.5, 5, 10, 30, 60, 300] });
        _writeWaits = meter.CreateHistogram(
            "terrabase.replication.write_wait", "s", "Time client writes waited for their peers' durable acknowledgment, by result: acknowledged, degraded (a failing peer was skipped) or timeout.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10] });
    }

    public void Sent(V1.CommittedGroupBatch batch)
    {
        _sentGroups.Add(1);
        _sentPlainBytes.Add(batch.DecodedApplicationBytes);
        _sentEncodedBytes.Add(batch.EncodedApplicationBytes);
    }

    /// <summary>
    /// Reports <paramref name="lag"/>: committed change groups that a peer has not durably acknowledged, summed
    /// over peers and database incarnations. Zero is meaningful only once persisted heads are initialized.
    /// </summary>
    public void ObserveDurableLag(Func<long> lag, Func<int> initialized)
    {
        _meter.CreateObservableGauge(
            "terrabase.replication.sender.durable_lag", lag, "{group}", "Committed change groups that a peer has not durably acknowledged.");
        _meter.CreateObservableGauge(
            "terrabase.replication.sender.initialized", initialized, "{state}", "One once all existing journal heads are measured, zero until then.");
    }

    public void Received(string result) => _receivedGroups.Add(1, new KeyValuePair<string, object?>("result", result));

    public void Converged(int peer, int database, double seconds) =>
        _convergence.Record(seconds, new KeyValuePair<string, object?>("peer", peer), new KeyValuePair<string, object?>("database", database));

    public void ObserveStatus(
        Func<IEnumerable<Measurement<double>>> lag, Func<IEnumerable<Measurement<double>>> diverged,
        Func<IEnumerable<Measurement<double>>> catchup, Func<IEnumerable<Measurement<long>>> peerLag, Func<int> omitted)
    {
        _meter.CreateObservableGauge("terrabase.replication.database.lag", lag, "{group}", "Durable lag by configured peer and reusable database slot.");
        _meter.CreateObservableGauge("terrabase.replication.database.diverged", diverged, description: "One while a detected journal conflict has not recovered.");
        _meter.CreateObservableGauge("terrabase.replication.database.catchup", catchup, "s", "Age of the observed outstanding backlog; zero when no catch-up is timed.");
        _meter.CreateObservableGauge("terrabase.replication.observability.omitted", omitted, "{database}", "Databases omitted from bounded detail; still included in peer and total lag.");
        _meter.CreateObservableGauge("terrabase.replication.peer.lag", peerLag, "{group}", "Durable lag summed over all databases for each configured peer.");
    }

    public void Discovered() => _discoveryPasses.Add(1);

    public void PrefixMismatch() => _prefixMismatches.Add(1);

    public void DetectedDivergence(string reason) => _divergenceDetections.Add(1, new KeyValuePair<string, object?>("reason", reason));
    public void Repaired(uint keys) => _repairedKeys.Add(keys);

    public void CreditWait() => _creditWaits.Add(1);

    /// <param name="waited">How long the write waited.</param>
    /// <param name="result"><c>acknowledged</c>, <c>degraded</c> or <c>timeout</c>.</param>
    public void WriteWaited(TimeSpan waited, string result) =>
        _writeWaits.Record(waited.TotalSeconds, new KeyValuePair<string, object?>("result", result));
}
