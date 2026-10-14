using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>
/// The waits of admitted write attempts for an eligible remote ack (decision 0013a sections 1, 2 and 7). An eligible ack
/// reports the range of positions (From, To] of one source incarnation that its peer received in live batches; a refill or
/// a stored resume position never widens it, so an ack is never read as a prefix. A registered wait is confirmed when the
/// ack arrives. An attempt is admitted before it executes, so for an ack that arrives before its waiter registers the
/// database keeps, only while it has admitted attempts, the union of the newest incarnation's acked ranges. Bounded by
/// 4,096 attempts per machine, 256 per database and 16 kept ranges per database; past 16 the lowest range is dropped, and
/// an attempt whose position only it held answers outcome unknown at the bound.
/// </summary>
internal sealed class RemoteAckWaiter(TimeProvider time)
{
    public const int MaxPerMachine = 4096;
    public const int MaxPerDatabase = 256;
    public const int MaxRanges = 16;
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static readonly Unavailable Busy = new("too many writes wait for another region's confirmation; this write executed nothing, retry later", "confirmation_busy");

    private readonly Lock _gate = new();
    private readonly Dictionary<TenantContext, Admitted> _databases = [];
    private int _total;

    /// <summary>Takes a waiter slot for one attempt of <paramref name="database"/>, or refuses it past the bounds.</summary>
    public RemoteAdmission TryAdmit(TenantContext database)
    {
        database = Key(database);
        lock (_gate)
        {
            var admitted = _databases.GetValueOrDefault(database);
            if (_total >= MaxPerMachine || admitted?.Count >= MaxPerDatabase)
            {
                return Busy;
            }

            admitted ??= _databases[database] = new Admitted();
            admitted.Count++;
            _total++;
        }

        return new RemoteLease(() => Release(database));
    }

    /// <summary>
    /// An eligible ack: a peer of another placed region holds durably every own group of <paramref name="incarnation"/>
    /// after <paramref name="from"/> up to <paramref name="to"/>, each received in a live batch.
    /// </summary>
    public void Acked(TenantContext database, Ulid incarnation, long from, long to)
    {
        database = Key(database);
        lock (_gate)
        {
            if (!_databases.TryGetValue(database, out var admitted))
            {
                return;
            }

            foreach (var waiting in admitted.Pending)
            {
                waiting.Confirmed |= waiting.Marker.Incarnation == incarnation && from < waiting.Marker.ChangeId && waiting.Marker.ChangeId <= to;
            }

            // Another incarnation's ack replaces the kept ranges: an attempt of the one replaced that has not registered yet
            // answers outcome unknown at the bound.
            if (admitted.Incarnation != incarnation)
            {
                admitted.Incarnation = incarnation;
                admitted.Ranges.Clear();
            }

            Keep(admitted.Ranges, from, to);
            Wake(admitted);
        }
    }

    /// <summary>A link was lost: every waiter checks whether an eligible link remains.</summary>
    public void Lost()
    {
        lock (_gate)
        {
            foreach (var admitted in _databases.Values)
            {
                Wake(admitted);
            }
        }
    }

    /// <summary>
    /// Whether an eligible ack covers <paramref name="marker"/> within the bound; false once the bound passes, or after a
    /// lost link when <paramref name="eligible"/> says no eligible link remains. The caller holds an admitted lease.
    /// </summary>
    public async ValueTask<bool> WaitAsync(TenantContext database, CommitMarker marker, Func<bool> eligible, CancellationToken cancellationToken)
    {
        database = Key(database);
        var deadline = time.GetTimestamp() + (long)(Bound.TotalSeconds * time.TimestampFrequency);
        var waiting = new Waiting(marker);
        Admitted admitted;
        lock (_gate)
        {
            admitted = _databases[database];
            if (admitted.Incarnation == marker.Incarnation && admitted.Ranges.Any(range => range.From < marker.ChangeId && marker.ChangeId <= range.To))
            {
                return true;
            }

            // Bounded by the admitted attempts: the caller holds one.
            admitted.Pending.Add(waiting);
        }

        try
        {
            while (true)
            {
                Task signal;
                lock (_gate)
                {
                    if (waiting.Confirmed)
                    {
                        return true;
                    }

                    signal = admitted.Signal.Task;
                }

                var left = TimeSpan.FromSeconds((double)(deadline - time.GetTimestamp()) / time.TimestampFrequency);
                if (left <= TimeSpan.Zero || !eligible())
                {
                    return false;
                }

                try
                {
                    await signal.WaitAsync(left, time, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Checked once more: an ack may have landed with the bound.
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _ = admitted.Pending.Remove(waiting);
            }
        }
    }

    // Adds (from, to] to the disjoint ranges, merging every range it overlaps or touches; past MaxRanges the lowest goes.
    // Also the shape frontier's persisted ranges (FR-25). The caller holds _gate for a database's ranges.
    internal static void Keep(List<(long From, long To)> ranges, long from, long to)
    {
        for (var i = ranges.Count - 1; i >= 0; i--)
        {
            if (from <= ranges[i].To && ranges[i].From <= to)
            {
                (from, to) = (Math.Min(from, ranges[i].From), Math.Max(to, ranges[i].To));
                ranges.RemoveAt(i);
            }
        }

        ranges.Add((from, to));
        if (ranges.Count > MaxRanges)
        {
            // The lowest positions are the least likely to belong to an attempt that has not registered yet.
            _ = ranges.Remove(ranges.MinBy(range => range.To));
        }
    }

    // The caller holds _gate.
    private static void Wake(Admitted admitted)
    {
        admitted.Signal.TrySetResult();
        admitted.Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void Release(TenantContext database)
    {
        lock (_gate)
        {
            _total--;
            if (--_databases[database].Count == 0)
            {
                _ = _databases.Remove(database);
            }
        }
    }

    // Plans can change and replication tenants omit them; only the ids identify the database.
    private static TenantContext Key(TenantContext database) => database with { Plan = PlanTier.Hobby };

    private sealed class Admitted
    {
        public int Count { get; set; }

        // The union of the newest incarnation's eligible acked ranges, while attempts are admitted.
        public Ulid Incarnation { get; set; }

        public List<(long From, long To)> Ranges { get; } = [];

        // The attempts waiting now; an ack confirms them directly, so dropping a kept range never unconfirms one.
        public List<Waiting> Pending { get; } = [];

        public TaskCompletionSource Signal { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Waiting(CommitMarker marker)
    {
        public CommitMarker Marker { get; } = marker;

        public bool Confirmed { get; set; }
    }
}
