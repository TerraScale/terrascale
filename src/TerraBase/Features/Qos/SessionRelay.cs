using TerraBase.Features.Sql;

namespace TerraBase.Features.Qos;

internal sealed class SessionRelay(TimeProvider time) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Queue<(char Reply, long Sent)> _pending = new();
    private readonly SqlTransactionClock _transaction = new(time);
    private SessionMessageDeadline? _waiting;
    private TaskCompletionSource? _settled;
    private bool _inTransaction;
    private bool _failed;

    public void Dispose()
    {
        lock (_gate)
        {
            _waiting?.Dispose();
            _waiting = null;
        }
    }

    public SessionMessageDeadline Wait()
    {
        lock (_gate)
        {
            _waiting = new SessionMessageDeadline(time, _inTransaction ? _transaction.Remaining : null, _pending.Count > 0);
            return _waiting;
        }
    }

    public void Sent(char reply, bool begins = false)
    {
        lock (_gate)
        {
            _waiting = null;
            if (begins)
            {
                _transaction.Start();
            }

            if (reply != '\0' && (!_failed || reply == 'Z'))
            {
                if (_pending.Count >= 256)
                {
                    throw new InvalidDataException("too many pending relay messages");
                }

                if (_pending.Count == 0)
                {
                    _settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                _pending.Enqueue((reply, time.GetTimestamp()));
            }
        }
    }

    public Task DrainAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return (_settled?.Task ?? Task.CompletedTask).WaitAsync(cancellationToken);
        }
    }

    public void Answered(char reply, bool? transaction = null)
    {
        lock (_gate)
        {
            if (transaction is { } open)
            {
                _inTransaction = open;
                if (open)
                {
                    _transaction.Start(_pending.TryPeek(out var first) ? first.Sent : null);
                }
                else
                {
                    _transaction.End();
                }
            }

            if (reply == 'Z')
            {
                _failed = false;
            }

            if (reply == 'E')
            {
                _failed = true;
                while (_pending.TryPeek(out var next) && next.Reply != 'Z')
                {
                    _pending.Dequeue();
                }
            }
            else if (_pending.TryPeek(out var next) && (next.Reply == reply || next.Reply == 'T' && reply == 'n'
                || next.Reply == 'C' && reply is 's' or 'I'))
            {
                _pending.Dequeue();
            }

            if (_pending.Count == 0)
            {
                _settled?.TrySetResult();
                _waiting?.Resume(_inTransaction ? _transaction.Remaining : null);
            }
        }
    }
}
