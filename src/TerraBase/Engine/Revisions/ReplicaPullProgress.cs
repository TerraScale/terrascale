namespace TerraBase.Engine.Revisions;

// Owned by one open revision ledger and protected by its gate. These are observations, never durable application data.
// Keep at most eight placed replicas and 256 commit boundaries; an expired boundary produces unknown lag.
internal sealed class ReplicaPullProgress
{
    private const int MaxCommits = 256;
    private readonly Queue<(DatabaseRevision Revision, DateTimeOffset At)> _commits = new();
    private readonly Dictionary<string, (DatabaseRevision Revision, DateTimeOffset At)> _pulls = new(StringComparer.Ordinal);
    private DatabaseRevision? _baseline;

    internal long Bytes => _commits.Count * 64L + _pulls.Count * 256L;

    internal void Reset(DatabaseRevision revision)
    {
        _commits.Clear();
        _pulls.Clear();
        _baseline = revision;
    }

    internal void Committed(DatabaseRevision revision, DateTimeOffset at)
    {
        if (_baseline is null || _baseline.Value.Incarnation != revision.Incarnation)
        {
            Reset(revision);
            return;
        }

        _commits.Enqueue((revision, at));
        if (_commits.Count > MaxCommits)
        {
            _baseline = _commits.Dequeue().Revision;
        }
    }

    internal void Pulled(string region, DatabaseRevision revision, DateTimeOffset at)
    {
        // A slower concurrent stream cannot replace a newer successfully served revision.
        if (_pulls.TryGetValue(region, out var previous) && previous.Revision.Incarnation == revision.Incarnation
            && Compare(previous.Revision, revision) > 0)
        {
            return;
        }

        if (_pulls.ContainsKey(region) || _pulls.Count < 8)
        {
            _pulls[region] = (revision, at);
        }
    }

    internal IReadOnlyList<ReplicaPullObservation> Read(DatabaseRevision current, IReadOnlyList<string> regions, DateTimeOffset now)
    {
        foreach (var removed in _pulls.Keys.Where(region => !regions.Contains(region)).ToArray())
        {
            _pulls.Remove(removed);
        }

        return [.. regions.Select(region =>
        {
            if (!_pulls.TryGetValue(region, out var pull))
            {
                return new ReplicaPullObservation(region, null, null, null);
            }

            double? lag = null;
            if (pull.Revision.Incarnation == current.Incarnation && _baseline is { } baseline && Compare(pull.Revision, baseline) >= 0)
            {
                var missing = _commits.FirstOrDefault(commit => Compare(commit.Revision, pull.Revision) > 0);
                lag = missing == default ? 0 : Math.Max(0, (now - missing.At).TotalMilliseconds);
            }

            return new ReplicaPullObservation(region, lag, pull.At, pull.Revision.ToString());
        })];
    }

    private static int Compare(DatabaseRevision left, DatabaseRevision right) => left.Generation != right.Generation
        ? left.Generation.CompareTo(right.Generation) : left.Frame.CompareTo(right.Frame);
}
