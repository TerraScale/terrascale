using System.Text;
using TerraBase.Features.Qos;
using TerraBase.Features.Regions;
using TerraBase.Features.Sql;
using TerraBase.Tenancy;

namespace TerraBase.Features.Postgres;

internal sealed class PostgresRelayAdmission(TenantContext tenant, IQosAdmission admission, TimeProvider time, CancellationTokenSource stop) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly Dictionary<string, bool> _statements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _portals = new(StringComparer.Ordinal);
    internal RelayMutation Mutation { get; } = new();
    private IDisposable? _lease;
    private ITimer? _timer;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal byte Status { get; set; } = (byte)'I';
    internal Task Ready => _ready.Task;
    internal void Open() => _ready.TrySetResult();
    internal int? RetryAfter { get; private set; }
    internal bool Pending { get { lock (_gate) { return _lease is not null; } } }

    internal async Task<bool> EnterAsync(char type, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (type is not ('Q' or 'P' or 'B' or 'E') || type != 'Q' && Pending)
        {
            Observe(type, body.Span);
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
        Observe(type, body.Span);
        return true;
    }

    internal async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        _turn.Release();
    }

    private void Observe(char type, ReadOnlySpan<byte> body)
    {
        switch (type)
        {
            case 'Q':
                Mutation.Sent(RelayMutation.MayMutateStatement(CString(ref body)));
                break;
            case 'P':
                var statement = CString(ref body);
                var sql = CString(ref body);
                if (_statements.TryGetValue(statement, out var previousStatement))
                {
                    _statements[statement] = previousStatement && !RelayMutation.MayMutateStatement(sql);
                }
                else if (_statements.Count < 256)
                {
                    _statements[statement] = !RelayMutation.MayMutateStatement(sql);
                }
                break;
            case 'B':
                var portal = CString(ref body);
                var prepared = CString(ref body);
                var read = _statements.TryGetValue(prepared, out var readOnlyStatement) && readOnlyStatement;
                if (_portals.TryGetValue(portal, out var previousPortal))
                {
                    _portals[portal] = previousPortal && read;
                }
                else if (_portals.Count < 256)
                {
                    _portals[portal] = read;
                }
                break;
            case 'E':
                Mutation.Sent(!_portals.TryGetValue(CString(ref body), out var readOnly) || !readOnly);
                break;
            case 'C':
                var kind = body[0];
                body = body[1..];
                var name = CString(ref body);
                var entries = kind == 'S' ? _statements : _portals;
                if (entries.ContainsKey(name))
                {
                    entries[name] = false;
                }
                break;
        }
    }

    private static string CString(ref ReadOnlySpan<byte> body)
    {
        var end = body.IndexOf((byte)0);
        if (end < 0)
        {
            throw new InvalidDataException("a Postgres string is unterminated");
        }
        var value = Encoding.UTF8.GetString(body[..end]);
        body = body[(end + 1)..];
        return value;
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
