using System.Diagnostics;

namespace TerraBase.Engine.Sqlite;

/// <summary>Opt-in, bounded native-call timing for integration diagnostics.</summary>
internal sealed class SqliteRunnerDiagnostics
{
    private const int Capacity = 64;
    private readonly Lock _gate = new();
    private readonly List<Call> _active = [with(Capacity)];
    private long _submitted;
    private long _begun;
    private long _completed;
    private long _resumed;
    private long _cancelled;
    private long _cancelledResumed;
    private long _aborted;
    private long _lost;
    private long _queueTicks;
    private long _callbackTicks;
    private long _resumeTicks;

    internal Call? Submit()
    {
        lock (_gate)
        {
            if (_active.Count == Capacity)
            {
                _lost++;
                return null;
            }

            var call = new Call(this, Stopwatch.GetTimestamp());
            _active.Add(call);
            _submitted++;
            return call;
        }
    }

    internal Snapshot Sample()
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            long queued = 0;
            long running = 0;
            long resuming = 0;
            long cancelResuming = 0;
            foreach (var call in _active)
            {
                if (call.Cancelled)
                {
                    cancelResuming = Math.Max(cancelResuming, now - call.CancelledAt);
                    continue;
                }

                if (call.CompletedAt != 0)
                {
                    resuming = Math.Max(resuming, now - call.CompletedAt);
                }
                else if (call.BegunAt != 0)
                {
                    running = Math.Max(running, now - call.BegunAt);
                }
                else
                {
                    queued = Math.Max(queued, now - call.SubmittedAt);
                }
            }

            return new(_submitted, _begun, _completed, _resumed, _cancelled, _cancelledResumed, _aborted, _lost,
                _queueTicks, _callbackTicks, _resumeTicks, queued, running, resuming, cancelResuming);
        }
    }

    internal readonly record struct Snapshot(
        long Submitted, long Begun, long Completed, long Resumed, long Cancelled, long CancelledResumed, long Aborted, long Lost,
        long QueueTicks, long CallbackTicks, long ResumeTicks, long OldestQueuedTicks, long OldestRunningTicks, long OldestResumeTicks,
        long OldestCancelledResumeTicks);

    internal sealed class Call(SqliteRunnerDiagnostics owner, long submittedAt) : IDisposable
    {
        private bool _retired;
        internal long SubmittedAt { get; } = submittedAt;
        internal long BegunAt { get; private set; }
        internal long CompletedAt { get; private set; }
        internal bool Cancelled { get; private set; }
        internal long CancelledAt { get; private set; }

        internal void Begin()
        {
            var now = Stopwatch.GetTimestamp();
            lock (owner._gate)
            {
                BegunAt = now;
                owner._begun++;
                owner._queueTicks += now - SubmittedAt;
            }
        }

        internal void Complete()
        {
            var now = Stopwatch.GetTimestamp();
            lock (owner._gate)
            {
                CompletedAt = now;
                owner._completed++;
                owner._callbackTicks += now - BegunAt;
            }
        }

        internal void Cancel()
        {
            var now = Stopwatch.GetTimestamp();
            lock (owner._gate)
            {
                Cancelled = true;
                CancelledAt = now;
                owner._cancelled++;
            }
        }

        public void Dispose()
        {
            var now = Stopwatch.GetTimestamp();
            lock (owner._gate)
            {
                if (_retired)
                {
                    return;
                }

                _retired = true;
                if (CompletedAt != 0)
                {
                    owner._resumed++;
                    owner._resumeTicks += now - CompletedAt;
                }
                else if (Cancelled)
                {
                    owner._cancelledResumed++;
                }
                else
                {
                    owner._aborted++;
                }

                _ = owner._active.Remove(this);
            }
        }
    }
}
