using TerraBase.Features.Qos;
using TerraBase.Features.Sql;
using TerraBase.Tenancy;

namespace TerraBase.Features.Postgres;

internal sealed class PostgresRelayAdmission(TenantContext tenant, IQosAdmission admission, TimeProvider time, CancellationTokenSource stop) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _turn = new(1, 1);
    private IDisposable? _lease;
    private ITimer? _timer;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal byte Status { get; set; } = (byte)'I';
    internal Task Ready => _ready.Task;
    internal void Open() => _ready.TrySetResult();
    internal int? RetryAfter { get; private set; }
    internal bool Pending { get { lock (_gate) { return _lease is not null; } } }

    internal async Task<bool> EnterAsync(char type, CancellationToken cancellationToken)
    {
        if (type is not ('Q' or 'P' or 'B' or 'E') || type != 'Q' && Pending)
        {
            return true;
        }

        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _lease = admission.TryEnter(tenant, out var retryAfter);
            if (_lease is null)
            {
                RetryAfter = retryAfter;
                _turn.Release();
                return false;
            }

            RetryAfter = null;
            _timer = time.CreateTimer(_ => stop.Cancel(), null, SqlLimits.MaxRequestTime, Timeout.InfiniteTimeSpan);
        }
        return true;
    }

    internal void End()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            if (_lease is not null)
            {
                _lease.Dispose();
                _turn.Release();
            }
            (_timer, _lease) = (null, null);
        }
    }

    public void Dispose()
    {
        End();
        _turn.Dispose();
    }
}
