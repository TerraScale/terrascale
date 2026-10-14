using System.Diagnostics.Metrics;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Shapes;

/// <summary>Bounded, tenant-free telemetry and memory admission shared by shape readers.</summary>
public interface IShapeMetrics
{
    /// <summary>Records one successful 200/304 shape response, including reads served from sealed data.</summary>
    void RecordRead();

    /// <summary>Adds or removes waiting requests; callers balance entry and exit in a finally block.</summary>
    void ChangeWaiting(long delta);

    /// <summary>Adds or removes running flights; callers balance entry and exit in a finally block.</summary>
    void ChangeFlights(long delta);

    /// <summary>Records a successfully published seal's bytes and elapsed time.</summary>
    void RecordSeal(long bytes, TimeSpan elapsed);

    /// <summary>Records the delay between a seal's publication and its mirror's completion.</summary>
    void RecordMirrorLag(TimeSpan lag);

    /// <summary>Records one shape 307 redirect.</summary>
    void RecordRedirect();

    /// <summary>
    /// Reserves bytes before allocating a seal, page or flight. Null means admission was refused.
    /// Hold the lease for the entire buffer or flight lifetime and dispose on completion, cancellation and error.
    /// The explicit tenant charges shared work to its owner; null is reserved for system work.
    /// </summary>
    IMemoryReservation? TryReserve(long bytes, TenantContext? tenant);
}

/// <summary>Uses the host-scoped TerraBase meter; no shape or tenant identity enters a metric.</summary>
internal sealed class ShapeMetrics : IShapeMetrics
{
    private readonly IMemoryBudget _memory;
    private readonly Counter<long> _reads;
    private readonly UpDownCounter<long> _waiting;
    private readonly UpDownCounter<long> _flights;
    private readonly Counter<long> _seals;
    private readonly Histogram<double> _sealSeconds;
    private readonly Counter<long> _sealedBytes;
    private readonly Histogram<double> _mirrorLag;
    private readonly Counter<long> _redirects;

    public ShapeMetrics(IMeterFactory meters, IMemoryBudget memory)
    {
        _memory = memory;
        var meter = meters.Create(Telemetry.MeterName);
        _reads = meter.CreateCounter<long>("terrabase.shape.reads", description: "Shape responses read, including sealed-data hits.");
        _waiting = meter.CreateUpDownCounter<long>("terrabase.shape.requests.waiting", description: "Shape requests currently waiting.");
        _flights = meter.CreateUpDownCounter<long>("terrabase.shape.flights.active", description: "Shape flights currently running.");
        _seals = meter.CreateCounter<long>("terrabase.shape.seals", description: "Shape seals successfully published.");
        _sealSeconds = meter.CreateHistogram<double>("terrabase.shape.seals.duration", "s", "Time to publish a shape seal.");
        _sealedBytes = meter.CreateCounter<long>("terrabase.shape.sealed_bytes", "By", "Bytes successfully published in shape seals.");
        _mirrorLag = meter.CreateHistogram<double>("terrabase.shape.mirror.lag", "s", "Delay from seal publication to mirror completion.");
        _redirects = meter.CreateCounter<long>("terrabase.shape.redirects", description: "Shape redirects issued.");
    }

    public void RecordRead() => _reads.Add(1);

    public void ChangeWaiting(long delta) => _waiting.Add(delta);

    public void ChangeFlights(long delta) => _flights.Add(delta);

    public void RecordSeal(long bytes, TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(elapsed.Ticks, nameof(elapsed));
        _seals.Add(1);
        _sealSeconds.Record(elapsed.TotalSeconds);
        _sealedBytes.Add(bytes);
    }

    public void RecordMirrorLag(TimeSpan lag)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lag.Ticks, nameof(lag));
        _mirrorLag.Record(lag.TotalSeconds);
    }

    public void RecordRedirect() => _redirects.Add(1);

    public IMemoryReservation? TryReserve(long bytes, TenantContext? tenant) =>
        _memory.TryReserve(MemoryLane.Foreground, bytes, tenant);
}
