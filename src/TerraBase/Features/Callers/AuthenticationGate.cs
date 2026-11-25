using System.Threading.RateLimiting;
using TerraBase.Hosting;

namespace TerraBase.Features.Callers;

internal sealed class AuthenticationGate : IDisposable
{
    public static readonly BackendUnavailable Busy = new("too many credentials are being verified; retry", "authentication_busy");

    private readonly ConcurrencyLimiter _lookups = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = 256,
        QueueLimit = 1024,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });

    public ValueTask<RateLimitLease> EnterAsync(CancellationToken cancellationToken) => _lookups.AcquireAsync(1, cancellationToken);

    public void Dispose() => _lookups.Dispose();
}
