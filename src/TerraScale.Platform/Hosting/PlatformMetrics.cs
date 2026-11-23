using System.Diagnostics.Metrics;

namespace TerraScale.Platform.Hosting;

public sealed class PlatformMetrics
{
    public const string MeterName = "TerraScale.Platform";

    private readonly Counter<long> _invocations;
    private readonly Histogram<double> _invocationDuration;
    private readonly Counter<long> _fileOperations;
    private readonly Counter<long> _shapeChunks;
    private readonly Counter<long> _shapePurges;
    private readonly Counter<long> _changefeedItems;

    public PlatformMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _invocations = meter.CreateCounter<long>("ts_platform.function.invocations", description: "Function invocations by trigger and outcome.");
        _invocationDuration = meter.CreateHistogram<double>("ts_platform.function.duration", "ms", "Function invocation wall time.");
        _fileOperations = meter.CreateCounter<long>("ts_platform.file.operations", description: "File storage operations by kind.");
        _shapeChunks = meter.CreateCounter<long>("ts_platform.shape.chunks", description: "Shape chunks sealed to object storage.");
        _shapePurges = meter.CreateCounter<long>("ts_platform.shape.cdn_purges", description: "Shape CDN purge requests by outcome.");
        _changefeedItems = meter.CreateCounter<long>("ts_platform.changefeed.items", description: "Changefeed items received by transport.");
    }

    public void Invocation(string trigger, bool succeeded, double milliseconds)
    {
        _invocations.Add(1, new KeyValuePair<string, object?>("trigger", trigger), new KeyValuePair<string, object?>("outcome", succeeded ? "ok" : "error"));
        _invocationDuration.Record(milliseconds, new KeyValuePair<string, object?>("trigger", trigger));
    }

    public void FileOperation(string operation) => _fileOperations.Add(1, new KeyValuePair<string, object?>("operation", operation));

    public void ShapeChunk() => _shapeChunks.Add(1);

    public void ShapePurge(bool accepted) => _shapePurges.Add(1, new KeyValuePair<string, object?>("outcome", accepted ? "accepted" : "failed"));

    public void ChangefeedItem(string transport) => _changefeedItems.Add(1, new KeyValuePair<string, object?>("transport", transport));
}
