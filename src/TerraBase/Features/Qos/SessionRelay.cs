using TerraBase.Features.Sql;
using TerraBase.Hosting;

namespace TerraBase.Features.Qos;

internal sealed class SessionRelay(TimeProvider time) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Queue<(char Reply, long Sent, bool Commits, long? Id, Action<bool>? Completed)> _pending = new();
    private readonly SqlTransactionClock _transaction = new(time);
    private SessionMessageDeadline? _waiting;
    private TaskCompletionSource? _settled;
    private Task? _ending;
    private bool _inTransaction;
    private bool _failed;
    private readonly SessionMessageDeadline _answer = new(time, null, executing: true);

    public SessionMessageDeadline Answer => _answer;

    public (bool Transaction, bool Pending) State
    {
        get
        {
            lock (_gate)
            {
                return (_inTransaction, _pending.Count > 0);
            }
        }
    }

    public (bool Commits, long? Id) Pending
    {
        get
        {
            lock (_gate)
            {
                foreach (var call in _pending)
                {
                    if (call.Commits)
                    {
                        return (true, call.Id);
                    }
                }

                return (false, _pending.TryPeek(out var first) ? first.Id : null);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _answer.Dispose();
            _waiting?.Dispose();
            _waiting = null;
            while (_pending.TryDequeue(out var pending))
            {
                pending.Completed?.Invoke(false);
            }
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

    public void Sent(char reply, bool begins = false, bool commits = false, long? id = null, Action<bool>? completed = null)
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
                    completed?.Invoke(false);
                    throw new InvalidDataException("too many pending relay messages");
                }

                if (_pending.Count == 0)
                {
                    _answer.ExpectAnswer(true);
                    _settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                _pending.Enqueue((reply, time.GetTimestamp(), commits, id, completed));
            }
            else
            {
                completed?.Invoke(false);
            }
        }
    }

    public Task EndAsync(Func<Task> end)
    {
        lock (_gate)
        {
            return _ending ??= end();
        }
    }

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        Task settled;
        lock (_gate)
        {
            settled = _settled?.Task ?? Task.CompletedTask;
        }

        using var closing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _answer.Token);
        closing.CancelAfter(Shutdown.DrainBudget - TimeSpan.FromMilliseconds(250));
        await settled.WaitAsync(closing.Token).ConfigureAwait(false);
    }

    public void Answered(char reply, bool? transaction = null)
    {
        lock (_gate)
        {
            var pending = _pending.Count;
            var answers = _pending.TryPeek(out var expected) && (expected.Reply == reply || expected.Reply == 'T' && reply == 'n'
                || expected.Reply == 'C' && reply is 's' or 'I');
            if (answers || reply == 'E' && expected.Reply != 'Z')
            {
                _answer.Complete();
            }
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
                    _pending.Dequeue().Completed?.Invoke(false);
                }
            }
            else if (answers)
            {
                _pending.Dequeue().Completed?.Invoke(true);
            }

            if (_pending.Count != pending)
            {
                _answer.ExpectAnswer(_pending.Count > 0);
            }

            if (_pending.Count == 0)
            {
                _settled?.TrySetResult();
                _waiting?.Resume(_inTransaction ? _transaction.Remaining : null);
            }
        }
    }
}
