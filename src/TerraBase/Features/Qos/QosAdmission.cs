using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;
using TerraBase.Hosting;

namespace TerraBase.Features.Qos;

internal sealed class QosAdmission : IAsyncDisposable
{
    public const int PermitsPerDatabase = 64;

    public const int QueuePerDatabase = 256;

    private readonly PartitionedRateLimiter<Ulid> _limiter = PartitionedRateLimiter.Create<Ulid, Ulid>(static databaseId =>
        RateLimitPartition.GetConcurrencyLimiter(databaseId, static _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = PermitsPerDatabase,
            QueueLimit = QueuePerDatabase,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        }));

    private readonly Counter<long> _refused;

    public QosAdmission(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _refused = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.qos.refused");
    }

    public ValueTask<RateLimitLease> AdmitAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var immediate = _limiter.AttemptAcquire(databaseId, 1);
        if (immediate.IsAcquired)
        {
            return ValueTask.FromResult(immediate);
        }

        immediate.Dispose();
        return AdmitSlowAsync(databaseId, cancellationToken);
    }

    private async ValueTask<RateLimitLease> AdmitSlowAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var lease = await _limiter.AcquireAsync(databaseId, 1, cancellationToken);
        if (!lease.IsAcquired)
        {
            _refused.Add(1);
        }

        return lease;
    }

    public ValueTask DisposeAsync() => _limiter.DisposeAsync();
}
