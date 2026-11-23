using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TerraBase.Continuity;

public enum ArchiveObjectKind
{
    Snapshot,
    Segment,
    Manifest,
}

public sealed class ContinuityMetrics
{
    public const string MeterName = "TerraScale.TerraBase";
    public const string BackupDisabledInstrument = "terrabase.backup.disabled";

    private readonly Counter<long> _uploaded;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _operations;
    private readonly Counter<long> _changefeedDelivered;
    private long _subscribers;

    public ContinuityMetrics(IMeterFactory meterFactory, ContinuityOptions options)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(options);
        var meter = meterFactory.Create(MeterName);
        var disabled = options.IsBackupEnabled ? 0 : 1;
        meter.CreateObservableGauge(BackupDisabledInstrument, () => disabled,
            description: "1 while continuous backup to the S3 archive is not configured on this node; no new recovery points are stored.");
        _uploaded = meter.CreateCounter<long>("terrabase.backup.uploaded", "By", "Bytes stored in the S3 archive, by kind (snapshot, segment, manifest).");
        _failures = meter.CreateCounter<long>("terrabase.backup.failures", description: "Archive passes of a database that failed.");
        _operations = meter.CreateCounter<long>("terrabase.backup.operations", description: "Restore, rewind and branch operations that finished, by kind and state.");
        _changefeedDelivered = meter.CreateCounter<long>("terrabase.changefeed.delivered", description: "Changefeed entries handed to subscribers.");
        meter.CreateObservableGauge("terrabase.changefeed.subscribers", () => Interlocked.Read(ref _subscribers), description: "Open changefeed subscriptions.");
    }

    public void Uploaded(ArchiveObjectKind kind, long bytes) =>
        _uploaded.Add(bytes, new KeyValuePair<string, object?>("kind", KindLabel(kind)));

    public void ArchiveFailed() => _failures.Add(1);

    public void OperationFinished(string kind, RestoreOperationState state) =>
        _operations.Add(1, new KeyValuePair<string, object?>("kind", kind), new KeyValuePair<string, object?>("state", ManifestCodec.StateLabel(state)));

    public void Delivered() => _changefeedDelivered.Add(1);

    public void SubscriberOpened() => Interlocked.Increment(ref _subscribers);

    public void SubscriberClosed() => Interlocked.Decrement(ref _subscribers);

    private static string KindLabel(ArchiveObjectKind kind) => kind switch
    {
        ArchiveObjectKind.Snapshot => "snapshot",
        ArchiveObjectKind.Segment => "segment",
        ArchiveObjectKind.Manifest => "manifest",
        _ => throw new UnreachableException(),
    };
}
